using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class MessageOperationSupervisionTests {
	[Fact]
	public async Task IngressKeepsAdmittingMessagesWhileAHandlerBlocksSynchronously() {
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { });
		await using var ingress = new MessageIngress(
			new InlineUiDispatcher(),
			router.RouteAsync,
			router.Disconnect,
			_ => { });
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var fast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var blocked = router.Host.Feature("blocked").Handle<Empty>("run", (_, _) => {
			entered.TrySetResult();
			release.Task.GetAwaiter().GetResult();
			return Task.CompletedTask;
		});
		using var responsive = router.Host.Feature("responsive").Handle<Empty>("run", (_, _) => {
			fast.TrySetResult();
			return Task.CompletedTask;
		});

		ingress.Enqueue(WebPeer.Native, HostEvent("blocked", "run"));
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		var elapsed = Stopwatch.StartNew();
		ingress.Enqueue(WebPeer.Native, HostEvent("responsive", "run"));
		await ingress.ProbeAsync(CancellationToken.None);
		elapsed.Stop();

		Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1));
		await fast.Task.WaitAsync(TimeSpan.FromSeconds(2));
		release.TrySetResult();
		await router.DrainAsync();
	}

	[Fact]
	public async Task IngressHealthProbeCrossesTheHostSequencingLane() {
		var laneEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseLane = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var routed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var dispatcher = new SerialUiDispatcher(_ => { });
		dispatcher.Post(() => {
			laneEntered.TrySetResult();
			releaseLane.Task.GetAwaiter().GetResult();
		});
		await laneEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		await using var ingress = new MessageIngress(
			dispatcher,
			(_, _) => {
				routed.TrySetResult();
				return Task.CompletedTask;
			},
			_ => { },
			_ => { });

		Task probe;
		try {
			ingress.Enqueue(WebPeer.Native, HostEvent("responsive", "run"));
			probe = ingress.ProbeAsync(CancellationToken.None);
			Assert.False(probe.IsCompleted);
		} finally {
			releaseLane.TrySetResult();
		}

		await probe.WaitAsync(TimeSpan.FromSeconds(2));
		await routed.Task.WaitAsync(TimeSpan.FromSeconds(2));
	}

	[Fact]
	public async Task BlockingRouterDiagnosticsCannotBlockIngress() {
		var transport = new RecordingTransport();
		var logEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseLog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => {
			logEntered.TrySetResult();
			releaseLog.Task.GetAwaiter().GetResult();
		});
		await using var ingress = new MessageIngress(
			new InlineUiDispatcher(),
			router.RouteAsync,
			router.Disconnect,
			_ => { });

		try {
			ingress.Enqueue(WebPeer.Native, "not-json");
			await logEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
			await ingress.ProbeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
		} finally {
			releaseLog.TrySetResult();
		}
	}

	[Fact]
	public async Task TimedOutHandlerIsDiagnosedSettledAndFencesItsEndpoint() {
		var transport = new RecordingTransport();
		var logs = new ConcurrentQueue<string>();
		var policy = new MessageExecutionPolicy(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(150));
		await using var router = new HostMessageRouter(
			transport,
			new InlineUiDispatcher(),
			logs.Enqueue,
			policy,
			TimeProvider.System);
		await using var endpoint = router.OpenSession(new SessionAddress("mobile", "i2"));
		endpoint.Activate();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handler = endpoint.Bus.Feature("lifecycle").Handle<Empty, Result>(
			"sync",
			async (_, _) => {
				entered.TrySetResult();
				await release.Task;
				return new Result(true);
			});
		var request = MessageEnvelope.SessionRequest(
			endpoint.Address,
			"request-7",
			"lifecycle",
			"sync",
			JsonSerializer.SerializeToElement(new Empty()));

		var dispatch = router.RouteAsync(new WebPeer("page"), request.ToJson());
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		await dispatch.WaitAsync(TimeSpan.FromSeconds(2));

		// The timeout settles the operation and writes its response on the supervision path, which can outlive
		// RouteAsync returning. Wait for it instead of racing the scheduler -- still exactly one response.
		await Wait.UntilAsync(() => transport.Envelopes(MessageKind.Response).Any());
		var response = Assert.Single(transport.Envelopes(MessageKind.Response));
		Assert.Contains("msg-", response.Error, StringComparison.Ordinal);
		Assert.Contains("lifecycle.sync", response.Error, StringComparison.Ordinal);
		Assert.Contains("stage handler", response.Error, StringComparison.Ordinal);
		await Wait.UntilAsync(() => transport.Events("notifications", "show")
			.Any(eventPayload => eventPayload.GetProperty("level").GetString() == "error"));
		Assert.Contains(
			transport.Events("notifications", "show"),
			eventPayload => eventPayload.GetProperty("level").GetString() == "busy");
		Assert.Contains(
			transport.Events("notifications", "show"),
			eventPayload => eventPayload.GetProperty("level").GetString() == "error"
				&& eventPayload.GetProperty("message").GetString()!.Contains("lifecycle.sync", StringComparison.Ordinal));
		Assert.Contains(logs, line => line.Contains("stage=handler", StringComparison.Ordinal));
		Assert.True(endpoint.Bus.Closed);
		var health = router.Health(ingressResponsive: true);
		Assert.False(health.Healthy);
		Assert.Equal("handler", health.LastFailure!.Stage);
		Assert.Equal("lifecycle", health.LastFailure.Feature);

		release.TrySetResult();
	}

	[Fact]
	public async Task BlockingLogCannotDelayTimeoutOrErrorNotification() {
		var transport = new RecordingTransport();
		var slowLogEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseSlowLog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseHandler = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var policy = new MessageExecutionPolicy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(120));
		await using var router = new HostMessageRouter(
			transport,
			new InlineUiDispatcher(),
			line => {
				if (line.Contains("[message] slow", StringComparison.Ordinal)) {
					slowLogEntered.TrySetResult();
					releaseSlowLog.Task.GetAwaiter().GetResult();
				}
			},
			policy,
			TimeProvider.System);
		await using var endpoint = router.OpenSession(new SessionAddress("blocked-log", "i1"));
		endpoint.Activate();
		using var handler = endpoint.Bus.Feature("lifecycle").Handle<Empty>(
			"sync",
			async (_, _) => await releaseHandler.Task);

		var dispatch = router.RouteAsync(
			new WebPeer("page"),
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"lifecycle",
				"sync",
				JsonSerializer.SerializeToElement(new Empty())).ToJson());
		try {
			await slowLogEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
			await dispatch.WaitAsync(TimeSpan.FromSeconds(2));
			await Wait.UntilAsync(() => transport.Events("notifications", "show")
				.Any(payload => payload.GetProperty("level").GetString() == "error"));
			Assert.False(router.Health(ingressResponsive: true).Healthy);
		} finally {
			releaseSlowLog.TrySetResult();
			releaseHandler.TrySetResult();
		}
	}

	[Fact]
	public async Task AfterResponseWorkRemainsUnderTheOriginalDeadline() {
		var transport = new RecordingTransport();
		// Flaked on main CI 2026-08-30 18:57 UTC (.NET tests, linux):
		// https://github.com/Kapps/weavie/actions/runs/33329458838/job/99305301184 — LastFailure.Stage
		// was "feature-queue" instead of "after-response". Root cause: the operation's watchdog starts
		// in MessageOperationRegistry.Start before the request's `admitted` TaskCompletionSource is
		// signaled; since that TCS uses RunContinuationsAsynchronously, resuming past `await admitted`
		// (and thus reaching MarkStage("handler-dispatch")) is a genuine thread-pool-scheduled
		// continuation, not inline execution. Under heavy parallel test-run contention that scheduling
		// gap can exceed a 150 ms deadline before dispatch even begins. Not a regression in the
		// watchdog itself. Widened to give real headroom over that scheduling gap.
		var policy = new MessageExecutionPolicy(TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(1500));
		await using var router = new HostMessageRouter(
			transport,
			new InlineUiDispatcher(),
			_ => { },
			policy,
			TimeProvider.System);
		await using var endpoint = router.OpenSession(new SessionAddress("a", "a1"));
		endpoint.Activate();
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handler = endpoint.Bus.Feature("lifecycle").HandleAfterResponse<Empty, Result>(
			"finish",
			(_, _) => Task.FromResult(new ResponseWithCompletion<Result>(
				new Result(true),
				async _ => await release.Task)));

		await router.RouteAsync(
			new WebPeer("page"),
			MessageEnvelope.SessionRequest(
				endpoint.Address,
				"after",
				"lifecycle",
				"finish",
				JsonSerializer.SerializeToElement(new Empty())).ToJson());
		await Wait.UntilAsync(() => router.Health(ingressResponsive: true).LastFailure is not null);

		var health = router.Health(ingressResponsive: true);
		Assert.False(health.Healthy);
		Assert.Equal("after-response", health.LastFailure!.Stage);
		Assert.Single(transport.Envelopes(MessageKind.Response));
		release.TrySetResult();
	}

	[Fact]
	public async Task BlockingSlowCallbackCannotDelayDeadline() {
		var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var handler = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var operation = new MessageOperation(
			"msg-blocked-diagnostics",
			new WebPeer("page"),
			MessageEnvelope.Event(
				MessageScope.Host,
				null,
				"test",
				"blockedDiagnostics",
				JsonSerializer.SerializeToElement(new Empty())),
			new MessageExecutionPolicy(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(120)),
			TimeProvider.System,
			_ => {
				slowEntered.TrySetResult();
				releaseSlow.Task.GetAwaiter().GetResult();
			},
			(_, _) => timedOut.TrySetResult(),
			(_, _) => { });
		operation.StartWatchdog();
		var supervised = operation.SuperviseAsync(() => handler.Task);

		// Flaked on main CI 2026-08-15 03:15 UTC (.NET tests, linux):
		// https://github.com/Kapps/weavie/actions/runs/31861262573/job/94954950325 — timed out
		// waiting 2s for slowEntered. Root cause: the "slow" callback blocks a thread-pool thread
		// synchronously (GetAwaiter().GetResult()), and under heavy parallel test-run contention the
		// pool can take longer than 2s to schedule that continuation. Not a regression in the
		// watchdog itself. Widened to 10s to absorb pool contention while still failing fast if the
		// watchdog genuinely stops firing.
		var flakeTolerance = TimeSpan.FromSeconds(10);
		try {
			await slowEntered.Task.WaitAsync(flakeTolerance);
			await timedOut.Task.WaitAsync(flakeTolerance);
			await Assert.ThrowsAsync<MessageOperationTimeoutException>(() => supervised);
			Assert.True(operation.HasTimedOut);
		} finally {
			releaseSlow.TrySetResult();
			handler.TrySetResult(true);
		}
	}

	[Fact]
	public async Task CompletionDoesNotWaitForBlockedSlowCallback() {
		var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		var operation = new MessageOperation(
			"msg-complete",
			new WebPeer("page"),
			MessageEnvelope.Event(
				MessageScope.Host,
				null,
				"test",
				"complete",
				JsonSerializer.SerializeToElement(new Empty())),
			new MessageExecutionPolicy(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(2)),
			TimeProvider.System,
			_ => {
				slowEntered.TrySetResult();
				releaseSlow.Task.GetAwaiter().GetResult();
			},
			(_, _) => { },
			(_, wasSlow) => completed.TrySetResult(wasSlow));
		operation.StartWatchdog();
		await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

		try {
			await Task.Run(operation.Complete).WaitAsync(TimeSpan.FromSeconds(1));
			Assert.True(await completed.Task.WaitAsync(TimeSpan.FromSeconds(1)));
		} finally {
			releaseSlow.TrySetResult();
		}
	}

	[Fact]
	public async Task TimeoutReservesTheResponseBeforeRunningItsCallback() {
		var timeoutEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseTimeout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var operation = new MessageOperation(
			"msg-response-race",
			new WebPeer("page"),
			MessageEnvelope.Request(
				MessageScope.Host,
				null,
				"request",
				"test",
				"responseRace",
				JsonSerializer.SerializeToElement(new Empty())),
			new MessageExecutionPolicy(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50)),
			TimeProvider.System,
			_ => { },
			(_, _) => {
				timeoutEntered.TrySetResult();
				releaseTimeout.Task.GetAwaiter().GetResult();
			},
			(_, _) => { });
		operation.StartWatchdog();

		try {
			await timeoutEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
			Assert.True(operation.HasTimedOut);
			Assert.True(operation.TimeoutOwnsResponse);
			Assert.False(operation.TrySettleResponse());
		} finally {
			releaseTimeout.TrySetResult();
		}
	}

	[Fact]
	public async Task DisposeCancelsPendingUiAdmission() {
		var dispatcher = new ManualUiDispatcher(paused: true);
		int routed = 0;
		var ingress = new MessageIngress(
			dispatcher,
			(_, _) => {
				Interlocked.Increment(ref routed);
				return Task.CompletedTask;
			},
			_ => { },
			_ => { });
		ingress.Enqueue(WebPeer.Native, HostEvent("test", "pending"));
		await dispatcher.WaitForPostAsync().WaitAsync(TimeSpan.FromSeconds(2));

		await ingress.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
		dispatcher.RunPending();

		Assert.Equal(0, Volatile.Read(ref routed));
	}

	[Fact]
	public async Task DisposeRejectsAProbeWaitingForUiAdmission() {
		var dispatcher = new ManualUiDispatcher(paused: true);
		var ingress = new MessageIngress(
			dispatcher,
			(_, _) => Task.CompletedTask,
			_ => { },
			_ => { });
		var probe = ingress.ProbeAsync(CancellationToken.None);
		await dispatcher.WaitForPostAsync().WaitAsync(TimeSpan.FromSeconds(2));

		await ingress.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

		await Assert.ThrowsAsync<ObjectDisposedException>(() => probe);
	}

	[Fact]
	public async Task FailedAdmissionSettlesBeforeBlockingOrThrowingDiagnostics() {
		var logEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseLog = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var ingress = new MessageIngress(
			new RejectingUiDispatcher(),
			(_, _) => Task.CompletedTask,
			_ => { },
			_ => {
				logEntered.TrySetResult();
				releaseLog.Task.GetAwaiter().GetResult();
				throw new InvalidOperationException("diagnostic sink failed");
			});

		try {
			var probe = ingress.ProbeAsync(CancellationToken.None);
			await Assert.ThrowsAsync<InvalidOperationException>(
				() => probe.WaitAsync(TimeSpan.FromSeconds(2)));
			await logEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

			var firstDisposal = ingress.DisposeAsync().AsTask();
			var concurrentDisposal = ingress.DisposeAsync().AsTask();
			await Task.WhenAll(firstDisposal, concurrentDisposal).WaitAsync(TimeSpan.FromSeconds(2));
			await ingress.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
		} finally {
			releaseLog.TrySetResult();
			await ingress.DisposeAsync();
		}
	}

	private static string HostEvent(string feature, string name) =>
		MessageEnvelope.Event(
			MessageScope.Host,
			null,
			feature,
			name,
			JsonSerializer.SerializeToElement(new Empty())).ToJson();

	private sealed record Empty;

	private sealed record Result(bool Ok);

	private sealed class RecordingTransport : IWebTransportHub {
		private readonly ConcurrentQueue<string> _messages = [];

		public event Action<WebPeer, string>? MessageReceived { add { } remove { } }
		public event Action<WebPeer>? PeerDisconnected { add { } remove { } }

		public void Broadcast(WebTransportMessage message) => _messages.Enqueue(message.Json);

		public void Send(WebPeer peer, WebTransportMessage message) => _messages.Enqueue(message.Json);

		public IReadOnlyList<MessageEnvelope> Envelopes(MessageKind kind) => [.. _messages
			.Select(json => MessageEnvelope.TryParse(json, out var envelope) ? envelope : null)
			.Where(envelope => envelope?.Kind == kind)
			.Cast<MessageEnvelope>()];

		public IReadOnlyList<JsonElement> Events(string feature, string name) => [.. Envelopes(MessageKind.Event)
			.Where(envelope => envelope.Feature == feature && envelope.Name == name)
			.Select(envelope => envelope.Payload)];
	}
}
