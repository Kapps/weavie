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
		var policy = new MessageExecutionPolicy(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(150));
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
				async () => await release.Task)));

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

		try {
			await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
			await timedOut.Task.WaitAsync(TimeSpan.FromSeconds(2));
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

		public void Broadcast(string json) => _messages.Enqueue(json);

		public void Send(WebPeer peer, string json) => _messages.Enqueue(json);

		public IReadOnlyList<MessageEnvelope> Envelopes(MessageKind kind) => [.. _messages
			.Select(json => MessageEnvelope.TryParse(json, out var envelope) ? envelope : null)
			.Where(envelope => envelope?.Kind == kind)
			.Cast<MessageEnvelope>()];

		public IReadOnlyList<JsonElement> Events(string feature, string name) => [.. Envelopes(MessageKind.Event)
			.Where(envelope => envelope.Feature == feature && envelope.Name == name)
			.Select(envelope => envelope.Payload)];
	}
}
