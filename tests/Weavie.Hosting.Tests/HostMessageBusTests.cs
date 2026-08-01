using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostMessageBusTests {
	[Fact]
	public async Task EveryQueuedHandlerReentersTheUiDispatcher() {
		var errors = new ConcurrentQueue<Exception>();
		var dispatcher = new SerialUiDispatcher(errors.Enqueue);
		var uiThread = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
		dispatcher.Post(() => uiThread.SetResult(Environment.CurrentManagedThreadId));
		int expectedThread = await uiThread.Task;
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, dispatcher, _ => { });
		var threads = new ConcurrentQueue<int>();
		var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var feature = router.Host.Feature("native");
		using var first = feature.Handle<Increment>(
			"first",
			async (_, _) => {
				threads.Enqueue(Environment.CurrentManagedThreadId);
				firstEntered.SetResult();
				await releaseFirst.Task;
			});
		using var second = feature.Handle<Increment>("second", (_, _) => {
			threads.Enqueue(Environment.CurrentManagedThreadId);
			return Task.CompletedTask;
		});

		var firstDispatch = router.RouteAsync(
			new WebPeer("page"),
			HostEvent("first", new Increment(1)));
		await firstEntered.Task;
		var secondDispatch = router.RouteAsync(
			new WebPeer("page"),
			HostEvent("second", new Increment(2)));

		Assert.False(secondDispatch.IsCompleted);
		await Task.Run(releaseFirst.SetResult);
		await Task.WhenAll(firstDispatch, secondDispatch);

		Assert.Equal([expectedThread, expectedThread], threads);
		Assert.Empty(errors);
	}

	[Fact]
	public async Task HandlerFailureReturnsARequestErrorWithoutEscapingTheDispatcher() {
		var errors = new ConcurrentQueue<Exception>();
		var dispatcher = new SerialUiDispatcher(errors.Enqueue);
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, dispatcher, _ => { });
		using var handler = router.Host.Feature("native").Handle<Increment, Counter>(
			"fail",
			(_, _) => throw new InvalidOperationException("native failure"));

		await router.RouteAsync(
			new WebPeer("page"),
			MessageEnvelope.Request(
				MessageScope.Host,
				null,
				"request",
				"native",
				"fail",
				JsonSerializer.SerializeToElement(new Increment(1))).ToJson());

		Assert.True(MessageEnvelope.TryParse(Assert.Single(transport.Sent), out var response));
		Assert.Equal("native failure", response!.Error);
		Assert.Empty(errors);
	}

	private static string HostEvent(string name, Increment payload) =>
		MessageEnvelope.Event(
			MessageScope.Host,
			null,
			"native",
			name,
			JsonSerializer.SerializeToElement(payload)).ToJson();

	private sealed record Increment(int By);

	private sealed record Counter(int Value);

	private sealed class RecordingTransport : IWebTransportHub {
		private readonly ConcurrentQueue<string> _sent = [];

		public event Action<WebPeer, string>? MessageReceived { add { } remove { } }
		public event Action<WebPeer>? PeerDisconnected { add { } remove { } }

		public IReadOnlyList<string> Sent => [.. _sent];

		public void Broadcast(string json) {
		}

		public void Send(WebPeer peer, string json) => _sent.Enqueue(json);
	}
}
