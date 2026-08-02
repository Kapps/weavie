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
	public async Task CompletionCannotBeOvertakenByALateSlowNotification() {
		var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var transitions = new ConcurrentQueue<string>();
		var operation = new MessageOperation(
			"msg-race",
			new WebPeer("page"),
			MessageEnvelope.Event(
				MessageScope.Host,
				null,
				"test",
				"race",
				JsonSerializer.SerializeToElement(new Empty())),
			new MessageExecutionPolicy(TimeSpan.FromMilliseconds(20), TimeSpan.FromSeconds(2)),
			TimeProvider.System,
			_ => {
				slowEntered.TrySetResult();
				releaseSlow.Task.GetAwaiter().GetResult();
				transitions.Enqueue("slow");
			},
			(_, _) => transitions.Enqueue("timeout"),
			(_, wasSlow) => transitions.Enqueue($"complete:{wasSlow}"));
		operation.StartWatchdog();
		await slowEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

		var completionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var completion = Task.Run(() => {
			completionStarted.TrySetResult();
			operation.Complete();
		});
		await completionStarted.Task;
		Assert.False(completion.IsCompleted);
		releaseSlow.TrySetResult();
		await completion.WaitAsync(TimeSpan.FromSeconds(2));

		Assert.Equal(["slow", "complete:True"], transitions);
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
