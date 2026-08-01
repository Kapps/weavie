using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class SessionMessageBusTests {
	[Fact]
	public void EnvelopeParserRejectsContradictoryAddressingAndCorrelation() {
		string valid = MessageEnvelope.SessionEvent(
			new SessionAddress("a", "a1"),
			"dummy",
			"changed",
			JsonSerializer.SerializeToElement(new { })).ToJson();

		Assert.True(MessageEnvelope.TryParse(valid, out _));
		Assert.False(MessageEnvelope.TryParse(
			"""{"scope":"host","session":{"slot":"a","incarnation":"a1"},"kind":"event","requestId":null,"feature":"dummy","name":"changed","payload":{},"error":null}""",
			out _));
		Assert.False(MessageEnvelope.TryParse(
			"""{"scope":"session","session":{"slot":"","incarnation":"a1"},"kind":"event","requestId":null,"feature":"dummy","name":"changed","payload":{},"error":null}""",
			out _));
		Assert.False(MessageEnvelope.TryParse(
			"""{"scope":"session","session":{"slot":"a","incarnation":"a1"},"kind":"event","requestId":"unexpected","feature":"dummy","name":"changed","payload":{},"error":null}""",
			out _));
		Assert.False(MessageEnvelope.TryParse(
			"""{"scope":"HOST","session":null,"kind":"event","requestId":null,"feature":"dummy","name":"changed","payload":{},"error":null}""",
			out _));
		Assert.False(MessageEnvelope.TryParse("[]", out _));
		Assert.False(MessageEnvelope.TryParse(
			"""{"scope":"session","session":42,"kind":"event","requestId":null,"feature":"dummy","name":"changed","payload":{},"error":null}""",
			out _));
		Assert.False(MessageEnvelope.TryParse(
			"""{"scope":"host","session":null,"kind":"999","requestId":"request","feature":"dummy","name":"changed","payload":{},"error":null}""",
			out _));
	}

	[Fact]
	public async Task DummyFeatureRoutesByOwnedSessionWhileAnotherSessionIsSelected() {
		var replies = new ConcurrentQueue<(WebPeer Peer, string Json)>();
		var router = new SessionMessageRouter(
			(peer, json) => replies.Enqueue((peer, json)),
			_ => { });
		var address = new SessionAddress("a", "a1");
		await using var bus = new SessionMessageBus(address, _ => { }, (peer, json) =>
			replies.Enqueue((peer, json)), _ => { });
		router.Add(bus);
		int value = 0;
		using var registration = bus.Feature("dummy").Handle<Increment, Counter>(
			"increment",
			(request, _) => Task.FromResult(new Counter(value += request.By)));

		var request = MessageEnvelope.SessionRequest(
			address,
			"request-1",
			"dummy",
			"increment",
			JsonSerializer.SerializeToElement(new Increment(3)));
		await router.RouteAsync(new WebPeer("page-a"), request);

		Assert.Equal(3, value);
		var (peer, json) = Assert.Single(replies);
		Assert.Equal(new WebPeer("page-a"), peer);
		Assert.True(MessageEnvelope.TryParse(json, out var parsed));
		Assert.Equal(address, parsed!.Session);
		Assert.Equal(MessageKind.Response, parsed.Kind);
		Assert.Equal(3, parsed.Payload.GetProperty("value").GetInt32());
	}

	[Fact]
	public async Task ReusedSlotCannotReceiveAnOldIncarnationRequest() {
		var replies = new ConcurrentQueue<string>();
		var router = new SessionMessageRouter((_, json) => replies.Enqueue(json), _ => { });
		await using var current = new SessionMessageBus(
			new SessionAddress("main", "new"),
			_ => { },
			(_, json) => replies.Enqueue(json),
			_ => { });
		router.Add(current);
		int calls = 0;
		using var registration = current.Feature("dummy").Handle<Increment, Counter>(
			"increment",
			(request, _) => Task.FromResult(new Counter(calls += request.By)));

		await router.RouteAsync(
			new WebPeer("page"),
			MessageEnvelope.SessionRequest(
				new SessionAddress("main", "old"),
				"stale",
				"dummy",
				"increment",
				JsonSerializer.SerializeToElement(new Increment(1))));

		Assert.Equal(0, calls);
		Assert.True(MessageEnvelope.TryParse(Assert.Single(replies), out var parsed));
		Assert.Equal("The target session is not live.", parsed!.Error);
		Assert.Equal("old", parsed.Session!.Incarnation);
	}

	[Fact]
	public async Task DifferentFeaturesRunInParallelWhileOneFeatureRemainsSerialized() {
		var router = new SessionMessageRouter((_, _) => { }, _ => { });
		var address = new SessionAddress("a", "a1");
		await using var bus = new SessionMessageBus(address, _ => { }, (_, _) => { }, _ => { });
		router.Add(bus);
		var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var slowEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var fastFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var slow = bus.Feature("slow").Handle<Increment, Counter>(
			"run",
			async (request, _) => {
				slowEntered.SetResult();
				await releaseSlow.Task;
				return new Counter(request.By);
			});
		using var fast = bus.Feature("fast").Handle<Increment, Counter>(
			"run",
			(request, _) => {
				fastFinished.SetResult();
				return Task.FromResult(new Counter(request.By));
			});

		var slowTask = router.RouteAsync(
			new WebPeer("page"),
			MessageEnvelope.SessionRequest(
				address,
				"slow",
				"slow",
				"run",
				JsonSerializer.SerializeToElement(new Increment(1))));
		await slowEntered.Task;
		var fastTask = router.RouteAsync(
			new WebPeer("page"),
			MessageEnvelope.SessionRequest(
				address,
				"fast",
				"fast",
				"run",
				JsonSerializer.SerializeToElement(new Increment(1))));
		await fastFinished.Task;
		releaseSlow.SetResult();
		await Task.WhenAll(slowTask, fastTask);
	}

	[Fact]
	public async Task SerializedHandlersShareTheirFeatureLane() {
		var address = new SessionAddress("a", "a1");
		var router = new SessionMessageRouter((_, _) => { }, _ => { });
		await using var bus = new SessionMessageBus(address, _ => { }, (_, _) => { }, _ => { });
		router.Add(bus);
		var order = new ConcurrentQueue<string>();
		var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var first = bus.Feature("counter").Handle<Increment, Counter>(
			"increment",
			async (request, _) => {
				order.Enqueue("first-entered");
				firstEntered.SetResult();
				await releaseFirst.Task;
				order.Enqueue("first-finished");
				return new Counter(request.By);
			});
		using var second = bus.Feature("counter").Handle<Increment, Counter>(
			"reset",
			(request, _) => {
				order.Enqueue("second-entered");
				return Task.FromResult(new Counter(request.By));
			});

		var firstTask = router.RouteAsync(
			new WebPeer("page"),
			MessageEnvelope.SessionRequest(
				address,
				"first",
				"counter",
				"increment",
				JsonSerializer.SerializeToElement(new Increment(1))));
		await firstEntered.Task;
		var secondTask = router.RouteAsync(
			new WebPeer("page"),
			MessageEnvelope.SessionRequest(
				address,
				"second",
				"counter",
				"reset",
				JsonSerializer.SerializeToElement(new Increment(0))));

		try {
			Assert.Equal(["first-entered"], order);
		} finally {
			releaseFirst.SetResult();
		}
		await Task.WhenAll(firstTask, secondTask);

		Assert.Equal(["first-entered", "first-finished", "second-entered"], order);
	}

	[Fact]
	public async Task IdenticalRequestIdsFromDifferentPeersAreIndependentAndUnicast() {
		var replies = new ConcurrentQueue<(WebPeer Peer, string Json)>();
		var address = new SessionAddress("a", "a1");
		var router = new SessionMessageRouter(
			(peer, json) => replies.Enqueue((peer, json)),
			_ => { });
		await using var bus = new SessionMessageBus(
			address,
			_ => { },
			(peer, json) => replies.Enqueue((peer, json)),
			_ => { });
		router.Add(bus);
		using var handler = bus.Feature("counter").HandleConcurrent<Increment, Counter>(
			"read",
			(request, _) => Task.FromResult(new Counter(request.By)));
		var request = MessageEnvelope.SessionRequest(
			address,
			"same-id",
			"counter",
			"read",
			JsonSerializer.SerializeToElement(new Increment(1)));

		await Task.WhenAll(
			router.RouteAsync(new WebPeer("page-a"), request),
			router.RouteAsync(new WebPeer("page-b"), request));

		Assert.Equal(
			[new WebPeer("page-a"), new WebPeer("page-b")],
			replies.Select(reply => reply.Peer).OrderBy(peer => peer.Id));
	}

	[Fact]
	public async Task CancellationMustMatchTheOriginalFeatureAndName() {
		var address = new SessionAddress("a", "a1");
		var router = new SessionMessageRouter((_, _) => { }, _ => { });
		await using var bus = new SessionMessageBus(address, _ => { }, (_, _) => { }, _ => { });
		router.Add(bus);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handler = bus.Feature("counter").HandleConcurrent<Increment, Counter>(
			"wait",
			async (request, ct) => {
				using var registration = ct.Register(() => cancelled.TrySetResult());
				entered.SetResult();
				await release.Task;
				return new Counter(request.By);
			});
		var peer = new WebPeer("page");
		var dispatch = router.RouteAsync(
			peer,
			MessageEnvelope.SessionRequest(
				address,
				"request",
				"counter",
				"wait",
				JsonSerializer.SerializeToElement(new Increment(1))));
		await entered.Task;

		await router.RouteAsync(
			peer,
			MessageEnvelope.SessionCancel(address, "request", "other", "wait"));
		await router.RouteAsync(
			peer,
			MessageEnvelope.SessionCancel(address, "request", "counter", "other"));

		Assert.False(cancelled.Task.IsCompleted);
		release.SetResult();
		await dispatch;
		Assert.False(cancelled.Task.IsCompleted);
	}

	[Fact]
	public async Task DuplicateRequestIdDoesNotReplaceOrSettleTheOriginalRequest() {
		var replies = new ConcurrentQueue<string>();
		var address = new SessionAddress("a", "a1");
		var router = new SessionMessageRouter((_, json) => replies.Enqueue(json), _ => { });
		await using var bus = new SessionMessageBus(
			address,
			_ => { },
			(_, json) => replies.Enqueue(json),
			_ => { });
		router.Add(bus);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int calls = 0;
		using var handler = bus.Feature("counter").HandleConcurrent<Increment, Counter>(
			"wait",
			async (request, _) => {
				Interlocked.Increment(ref calls);
				entered.SetResult();
				await release.Task;
				return new Counter(request.By);
			});
		var peer = new WebPeer("page");
		var original = MessageEnvelope.SessionRequest(
			address,
			"request",
			"counter",
			"wait",
			JsonSerializer.SerializeToElement(new Increment(1)));
		var dispatch = router.RouteAsync(peer, original);
		await entered.Task;

		await router.RouteAsync(
			peer,
			MessageEnvelope.SessionRequest(
				address,
				"request",
				"counter",
				"wait",
				JsonSerializer.SerializeToElement(new Increment(2))));

		Assert.Equal(1, calls);
		Assert.Empty(replies);
		release.SetResult();
		await dispatch;
		Assert.True(MessageEnvelope.TryParse(Assert.Single(replies), out var response));
		Assert.Equal(1, response!.Payload.GetProperty("value").GetInt32());
	}

	[Fact]
	public async Task ViewRequestTargetsOnlyThePageBoundToTheExactSession() {
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { });
		await using var endpoint = router.OpenSession(new SessionAddress("a", "a1"));
		endpoint.Activate();
		var peer = new WebPeer("page-a");
		await router.RouteAsync(
			peer,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"view",
				"attach",
				ViewAttachment("page-a")).ToJson());

		var pending = router.RequestViewAsync<Increment, Counter>(
			endpoint.Address,
			"dummyView",
			"read",
			new Increment(4),
			CancellationToken.None);
		var (sentPeer, sentJson) = Assert.Single(transport.Sent);
		Assert.Equal(peer, sentPeer);
		Assert.True(MessageEnvelope.TryParse(sentJson, out var request));
		Assert.Equal(endpoint.Address, request!.Session);

		await router.RouteAsync(
			peer,
			MessageEnvelope.SessionResponse(
				endpoint.Address,
				request.RequestId!,
				request.Feature,
				request.Name,
				JsonSerializer.SerializeToElement(new Counter(4)),
				null).ToJson());

		Assert.Equal(4, (await pending).Value);
	}

	[Fact]
	public async Task NativeReloadReplacesTheViewGenerationAndSettlesItsOldRequest() {
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { });
		await using var endpoint = router.OpenSession(new SessionAddress("a", "a1"));
		endpoint.Activate();
		await router.RouteAsync(
			WebPeer.Native,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"view",
				"attach",
				ViewAttachment("old-page")).ToJson());
		var oldRequest = router.RequestViewAsync<Increment, Counter>(
			endpoint.Address,
			"dummyView",
			"read",
			new Increment(1),
			CancellationToken.None);

		await router.RouteAsync(
			WebPeer.Native,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"view",
				"attach",
				ViewAttachment("new-page")).ToJson());

		var detached = await Assert.ThrowsAsync<InvalidOperationException>(() => oldRequest);
		Assert.Contains("no longer attached", detached.Message, StringComparison.Ordinal);
		Assert.Contains(
			transport.Sent,
			sent => MessageEnvelope.TryParse(sent.Json, out var envelope)
				&& envelope is { Kind: MessageKind.Cancel });
		transport.Sent.Clear();

		var currentRequest = router.RequestViewAsync<Increment, Counter>(
			endpoint.Address,
			"dummyView",
			"read",
			new Increment(2),
			CancellationToken.None);
		Assert.True(MessageEnvelope.TryParse(Assert.Single(transport.Sent).Json, out var request));
		await router.RouteAsync(
			WebPeer.Native,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"view",
				"attach",
				ViewAttachment("new-page")).ToJson());
		await router.RouteAsync(
			WebPeer.Native,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"view",
				"detach",
				ViewAttachment("old-page")).ToJson());
		await router.RouteAsync(
			WebPeer.Native,
			MessageEnvelope.SessionResponse(
				endpoint.Address,
				request!.RequestId!,
				request.Feature,
				request.Name,
				JsonSerializer.SerializeToElement(new Counter(2)),
				null).ToJson());

		Assert.Equal(2, (await currentRequest).Value);
	}

	[Fact]
	public async Task ViewAuthorshipIsCapturedAtAdmissionAndRejectsAnUnboundPage() {
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { });
		await using var endpoint = router.OpenSession(new SessionAddress("a", "a1"));
		endpoint.Activate();
		var first = new WebPeer("page-a");
		var second = new WebPeer("page-b");
		await router.RouteAsync(
			first,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"view",
				"attach",
				ViewAttachment("page-a")).ToJson());
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var values = new ConcurrentQueue<int>();
		var editor = endpoint.Bus.Feature("editor");
		using var blocker = editor.Handle<Increment>(
			"block",
			async (_, _) => {
				entered.SetResult();
				await release.Task;
			});
		using var changed = editor.HandleOwned<Increment>(
			"changed",
			endpoint.View.IsBound,
			(message, _, _) => {
				values.Enqueue(message.By);
				return Task.CompletedTask;
			});
		var blocking = router.RouteAsync(
			first,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"editor",
				"block",
				JsonSerializer.SerializeToElement(new Increment(0))).ToJson());
		await entered.Task;

		var admittedBeforeSwitch = router.RouteAsync(
			first,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"editor",
				"changed",
				JsonSerializer.SerializeToElement(new Increment(1))).ToJson());
		await router.RouteAsync(
			second,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"view",
				"attach",
				ViewAttachment("page-b")).ToJson());
		await router.RouteAsync(
			first,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"editor",
				"changed",
				JsonSerializer.SerializeToElement(new Increment(2))).ToJson());
		var admittedAfterSwitch = router.RouteAsync(
			second,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"editor",
				"changed",
				JsonSerializer.SerializeToElement(new Increment(3))).ToJson());
		release.SetResult();
		await Task.WhenAll(blocking, admittedBeforeSwitch, admittedAfterSwitch);

		Assert.Equal([1, 3], values);
	}

	[Fact]
	public void DurableStateReplayIsUnicastToTheRequestingPeer() {
		var bridge = new FakeHostBridge();
		var bus = new SessionMessageBus(
			new SessionAddress("a", "a1"),
			bridge.Broadcast,
			bridge.Send,
			_ => { });
		var state = new SessionState(bus);
		state.Set("source", "document:1", "document", new { title = "One" });
		bridge.Clear();
		var peer = bus.Peer(new WebPeer("page-b"));

		state.Replay(peer.Target);

		var (sentPeer, sentJson) = Assert.Single(bridge.Sent);
		Assert.Equal(new WebPeer("page-b"), sentPeer);
		Assert.True(MessageEnvelope.TryParse(sentJson, out var replay));
		Assert.Equal("source", replay!.Feature);
		Assert.Equal("document", replay.Name);
		Assert.Equal("One", replay.Payload.GetProperty("title").GetString());
	}

	[Fact]
	public async Task MovingAPageBindingCannotMakeAViewRequestHitItsPreviousSession() {
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { });
		await using var first = router.OpenSession(new SessionAddress("a", "a1"));
		await using var second = router.OpenSession(new SessionAddress("b", "b1"));
		first.Activate();
		second.Activate();
		var peer = new WebPeer("page");
		await router.RouteAsync(
			peer,
			MessageEnvelope.SessionEvent(
				first.Address,
				"view",
				"attach",
				ViewAttachment("page")).ToJson());
		await router.RouteAsync(
			peer,
			MessageEnvelope.SessionEvent(
				second.Address,
				"view",
				"attach",
				ViewAttachment("page")).ToJson());

		await Assert.ThrowsAsync<InvalidOperationException>(async () =>
			await router.RequestViewAsync<Increment, Counter>(
				first.Address,
				"dummyView",
				"read",
				new Increment(1),
				CancellationToken.None));
	}

	[Fact]
	public async Task DetachingDuringViewRequestAdmissionCancelsTheExactRequest() {
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { });
		await using var endpoint = router.OpenSession(new SessionAddress("a", "a1"));
		endpoint.Activate();
		var peer = new WebPeer("page");
		await router.RouteAsync(
			peer,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"view",
				"attach",
				ViewAttachment("page")).ToJson());
		transport.Sending = (sentPeer, json) => {
			Assert.True(MessageEnvelope.TryParse(json, out var envelope));
			if (envelope!.Kind != MessageKind.Request) {
				return;
			}

			router.RouteAsync(
				sentPeer,
				MessageEnvelope.SessionEvent(
						endpoint.Address,
						"view",
						"detach",
						ViewAttachment("page")).ToJson()).GetAwaiter().GetResult();
		};

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			router.RequestViewAsync<Increment, Counter>(
				endpoint.Address,
				"dummyView",
				"read",
				new Increment(1),
				CancellationToken.None));

		Assert.Contains("no longer attached", error.Message, StringComparison.Ordinal);
		Assert.Contains(
			transport.Sent,
			sent => MessageEnvelope.TryParse(sent.Json, out var envelope)
				&& envelope is { Kind: MessageKind.Cancel });
	}

	[Fact]
	public async Task QuiescingASessionSettlesItsOutstandingViewRequest() {
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { });
		await using var endpoint = router.OpenSession(new SessionAddress("a", "a1"));
		endpoint.Activate();
		var peer = new WebPeer("page");
		await router.RouteAsync(
			peer,
			MessageEnvelope.SessionEvent(
				endpoint.Address,
				"view",
				"attach",
				ViewAttachment("page")).ToJson());
		var pending = router.RequestViewAsync<Increment, Counter>(
			endpoint.Address,
			"dummyView",
			"read",
			new Increment(1),
			CancellationToken.None);

		await endpoint.QuiesceAsync();

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
		Assert.Contains("no longer attached", error.Message, StringComparison.Ordinal);
		Assert.Contains(
			transport.Sent,
			sent => MessageEnvelope.TryParse(sent.Json, out var envelope)
				&& envelope is { Kind: MessageKind.Cancel });
	}

	[Fact]
	public async Task QuiescingDetachesInboundRoutingButAllowsFinalOwnedEvents() {
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { });
		await using var endpoint = router.OpenSession(new SessionAddress("a", "a1"));
		endpoint.Activate();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handler = endpoint.Bus.Feature("dummy").Handle<Increment, Counter>(
			"wait",
			async (request, ct) => {
				entered.SetResult();
				await Task.Delay(Timeout.InfiniteTimeSpan, ct);
				return new Counter(request.By);
			});
		var peer = new WebPeer("page");
		var dispatch = router.RouteAsync(
			peer,
			MessageEnvelope.SessionRequest(
				endpoint.Address,
				"running",
				"dummy",
				"wait",
				JsonSerializer.SerializeToElement(new Increment(1))).ToJson());
		await entered.Task;

		await endpoint.QuiesceAsync();
		await dispatch;
		endpoint.Bus.Feature("lifecycle").Publish("flushed", new Counter(7));
		await router.RouteAsync(
			peer,
			MessageEnvelope.SessionRequest(
				endpoint.Address,
				"late",
				"dummy",
				"wait",
				JsonSerializer.SerializeToElement(new Increment(1))).ToJson());

		Assert.True(MessageEnvelope.TryParse(Assert.Single(transport.Broadcasts), out var flushed));
		Assert.Equal("flushed", flushed!.Name);
		Assert.Equal(7, flushed.Payload.GetProperty("value").GetInt32());
		Assert.Contains(
			transport.Sent,
			sent => MessageEnvelope.TryParse(sent.Json, out var envelope)
				&& envelope is { RequestId: "late", Error: "The target session is not live." });
	}

	[Fact]
	public async Task AHandlerIsTrackedBeforeItsCodeCanBeginQuiescingTheEndpoint() {
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { });
		await using var endpoint = router.OpenSession(new SessionAddress("a", "a1"));
		endpoint.Activate();
		Task? quiesce = null;
		bool completedInsideHandler = true;
		using var handler = endpoint.Bus.Feature("dummy").Handle<Increment, Counter>(
			"close",
			(request, _) => {
				quiesce = endpoint.QuiesceAsync();
				completedInsideHandler = quiesce.IsCompleted;
				return Task.FromResult(new Counter(request.By));
			});

		await router.RouteAsync(
			new WebPeer("page"),
			MessageEnvelope.SessionRequest(
				endpoint.Address,
				"close",
				"dummy",
				"close",
				JsonSerializer.SerializeToElement(new Increment(1))).ToJson());
		await quiesce!;

		Assert.False(completedInsideHandler);
	}

	[Fact]
	public async Task AfterResponseWorkCanQuiesceTheEndpointThatCarriedItsReply() {
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { });
		await using var endpoint = router.OpenSession(new SessionAddress("a", "a1"));
		endpoint.Activate();
		var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handler = endpoint.Bus.Feature("dummy").HandleAfterResponse<Increment, Counter>(
			"close",
			(request, _) => Task.FromResult(
				new ResponseWithCompletion<Counter>(
					new Counter(request.By),
					async () => {
						Assert.Contains(
							transport.Sent,
							sent => MessageEnvelope.TryParse(sent.Json, out var envelope)
								&& envelope is { RequestId: "close", Kind: MessageKind.Response });
						await endpoint.QuiesceAsync();
						completed.SetResult();
					})));

		await router.RouteAsync(
			new WebPeer("page"),
			MessageEnvelope.SessionRequest(
				endpoint.Address,
				"close",
				"dummy",
				"close",
				JsonSerializer.SerializeToElement(new Increment(1))).ToJson());
		await completed.Task;
	}

	[Fact]
	public async Task LostPeerDuringReplyCannotFaultSessionQuiescenceOrSkipAfterResponseWork() {
		var logs = new List<string>();
		var transport = new RecordingTransport {
			Sending = (_, _) => throw new IOException("peer disconnected"),
		};
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), logs.Add);
		await using var endpoint = router.OpenSession(new SessionAddress("a", "a1"));
		endpoint.Activate();
		var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handler = endpoint.Bus.Feature("dummy").HandleAfterResponse<Increment, Counter>(
			"close",
			(request, _) => Task.FromResult(
				new ResponseWithCompletion<Counter>(
					new Counter(request.By),
					() => {
						completed.SetResult();
						return Task.CompletedTask;
					})));

		await router.RouteAsync(
			new WebPeer("gone"),
			MessageEnvelope.SessionRequest(
				endpoint.Address,
				"close",
				"dummy",
				"close",
				JsonSerializer.SerializeToElement(new Increment(1))).ToJson());
		await endpoint.QuiesceAsync();
		await completed.Task;

		Assert.Single(transport.Sent);
		Assert.Contains(logs, line => line.Contains("response delivery", StringComparison.Ordinal));
	}

	[Fact]
	public async Task SessionPublicationsWaitForCatalogActivationAndKeepTheirOrder() {
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { });
		await using var endpoint = router.OpenSession(new SessionAddress("a", "a1"));
		var feature = endpoint.Bus.Feature("dummy");

		feature.Publish("first", new Counter(1));
		feature.Publish("second", new Counter(2));

		Assert.Empty(transport.Broadcasts);
		endpoint.Activate();
		Assert.Equal(2, transport.Broadcasts.Count);
		Assert.True(MessageEnvelope.TryParse(transport.Broadcasts[0], out var first));
		Assert.True(MessageEnvelope.TryParse(transport.Broadcasts[1], out var second));
		Assert.Equal("first", first!.Name);
		Assert.Equal("second", second!.Name);
	}

	private sealed record Increment(int By);

	private sealed record Counter(int Value);

	private static JsonElement ViewAttachment(string pageEpoch) =>
		JsonSerializer.SerializeToElement(new { pageEpoch });

	private sealed class RecordingTransport : IWebTransportHub {
		public event Action<WebPeer, string>? MessageReceived { add { } remove { } }
		public event Action<WebPeer>? PeerDisconnected { add { } remove { } }

		public List<(WebPeer Peer, string Json)> Sent { get; } = [];

		public List<string> Broadcasts { get; } = [];

		public Action<WebPeer, string> Sending { get; set; } = static (_, _) => { };

		public void Broadcast(string json) => Broadcasts.Add(json);

		public void Send(WebPeer peer, string json) {
			Sent.Add((peer, json));
			Sending(peer, json);
		}
	}
}
