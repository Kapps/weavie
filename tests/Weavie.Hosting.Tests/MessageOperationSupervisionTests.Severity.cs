using System.Text.Json;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed partial class MessageOperationSupervisionTests {
	private static readonly MessageExecutionPolicy SeverityPolicy =
		new(TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));

	private sealed class SeverityHarness : IAsyncDisposable {
		private readonly WebPeer _peer = new("page");
		private int _request;

		private SeverityHarness(HostMessageRouter router, SessionEndpoint endpoint, RecordingTransport transport, ManualTimeProvider time) {
			Router = router;
			Endpoint = endpoint;
			Transport = transport;
			Time = time;
		}

		public HostMessageRouter Router { get; }

		public SessionEndpoint Endpoint { get; }

		public RecordingTransport Transport { get; }

		public ManualTimeProvider Time { get; }

		public MessageFeatureChannel Feature => Endpoint.Bus.Feature("work");

		public static SeverityHarness Start() {
			var transport = new RecordingTransport();
			var time = new ManualTimeProvider();
			var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { }, SeverityPolicy, time);
			var endpoint = router.OpenSession(new SessionAddress("slot", "i1"));
			endpoint.Activate();
			return new SeverityHarness(router, endpoint, transport, time);
		}

		public IDisposable Handle(string name, MessageActivity activity, Func<CancellationToken, Task<Result>> run) =>
			Feature.Handle(name, activity, OperationSupervisionJson.Default.Empty, OperationSupervisionJson.Default.Result, (_, ct) => run(ct));

		public Task Send(string name) => Router.RouteAsync(_peer, MessageEnvelope.SessionRequest(
			Endpoint.Address,
			$"request-{Interlocked.Increment(ref _request)}",
			"work",
			name,
			JsonSerializer.SerializeToElement(new Empty())).ToJson());

		public IEnumerable<JsonElement> Errors => Transport.Events("notifications", "show")
			.Where(toast => toast.GetProperty("level").GetString() == "error");

		public async ValueTask DisposeAsync() {
			await Endpoint.DisposeAsync();
			await Router.DisposeAsync();
		}
	}

	private static Task<Result> Blocked(TaskCompletionSource entered, TaskCompletionSource release) {
		entered.TrySetResult();
		return release.Task.ContinueWith(_ => new Result(true), TaskScheduler.Default);
	}

	private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

	[Fact]
	public async Task AStuckActionHoldsItsLaneWithoutClosingTheSession() {
		await using var h = SeverityHarness.Start();
		TaskCompletionSource entered = Signal(), release = Signal();
		bool successorRan = false;
		using var stuck = h.Handle("stuck", MessageActivity.ActionFailed("Posting"), _ => Blocked(entered, release));
		using var next = h.Handle("next", MessageActivity.ActionFailed("Reading"), _ => {
			successorRan = true;
			return Task.FromResult(new Result(true));
		});
		using var other = h.Endpoint.Bus.Feature("other").Handle(
			"ping", MessageActivity.ActionFailed("Pinging"), OperationSupervisionJson.Default.Empty, OperationSupervisionJson.Default.Result,
			(_, _) => Task.FromResult(new Result(true)));

		var running = h.Send("stuck");
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		h.Time.Advance(SeverityPolicy.Deadline);
		await running.WaitAsync(TimeSpan.FromSeconds(2));

		var queued = h.Send("next");
		Assert.False(successorRan);
		h.Time.Advance(SeverityPolicy.Deadline);
		await queued.WaitAsync(TimeSpan.FromSeconds(2));
		release.TrySetResult();
		await Wait.UntilAsync(() => h.Errors.Any(toast => toast.GetProperty("message").GetString()!.Contains("Posting is stuck", StringComparison.Ordinal)));
		Assert.False(successorRan);
		Assert.False(h.Endpoint.Bus.Closed);
		Assert.Null(h.Endpoint.Bus.ReloadReason);
		Assert.Contains(h.Errors, toast => toast.TryGetProperty("action", out var action)
			&& action.GetProperty("commandId").GetString() == "weavie.session.reloadPrompt");
	}

	[Fact]
	public async Task ADroppableReleasesItsLaneQuietlyAtTheDeadline() {
		await using var h = SeverityHarness.Start();
		TaskCompletionSource entered = Signal(), release = Signal(), successor = Signal();
		using var stuck = h.Handle("stuck", MessageActivity.Droppable("Spelling"), _ => Blocked(entered, release));
		using var next = h.Handle("next", MessageActivity.ActionFailed("Reading"), _ => {
			successor.TrySetResult();
			return Task.FromResult(new Result(true));
		});

		var running = h.Send("stuck");
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		h.Time.Advance(SeverityPolicy.Deadline);
		await running.WaitAsync(TimeSpan.FromSeconds(2));
		await h.Send("next").WaitAsync(TimeSpan.FromSeconds(2));

		await successor.Task.WaitAsync(TimeSpan.FromSeconds(2));
		release.TrySetResult();
		Assert.Empty(h.Errors);
		Assert.Null(h.Router.Health(ingressResponsive: true).LastFailure);
	}

	[Fact]
	public async Task ACriticalOperationRequiresAReloadEvenIfItOnlyQueued() {
		await using var h = SeverityHarness.Start();
		TaskCompletionSource entered = Signal(), release = Signal();
		int changes = 0;
		h.Endpoint.Bus.ReloadRequiredChanged += () => Interlocked.Increment(ref changes);
		using var stuck = h.Handle("stuck", MessageActivity.ActionFailed("Reading a file"), _ => Blocked(entered, release));
		using var save = h.Handle("save", MessageActivity.SessionCritical("Saving a file"), _ => Task.FromResult(new Result(true)));

		var running = h.Send("stuck");
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		var queued = h.Send("save");
		h.Time.Advance(SeverityPolicy.Deadline);
		await Task.WhenAll(running, queued).WaitAsync(TimeSpan.FromSeconds(2));
		await Wait.UntilAsync(() => Volatile.Read(ref changes) == 1);

		Assert.Equal("Saving a file didn't start within 0.1 seconds because Reading a file is stuck.", h.Endpoint.Bus.ReloadReason);
		Assert.Equal(1, changes);
		Assert.False(h.Endpoint.Bus.Closed);
		release.TrySetResult();
	}

	[Fact]
	public async Task ALateSuccessClearsItsNoticeAndRunsItsCompletionCancelled() {
		await using var h = SeverityHarness.Start();
		TaskCompletionSource entered = Signal(), release = Signal();
		var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var slow = h.Feature.HandleAfterResponse(
			"slow", MessageActivity.ActionFailed("Posting"), OperationSupervisionJson.Default.Empty, OperationSupervisionJson.Default.Result,
			async (_, _) => {
				await Blocked(entered, release);
				return new ResponseWithCompletion<Result>(new Result(true), ct => {
					completion.TrySetResult(ct.IsCancellationRequested);
					return Task.CompletedTask;
				});
			});

		var running = h.Send("slow");
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		h.Time.Advance(SeverityPolicy.Deadline);
		await running.WaitAsync(TimeSpan.FromSeconds(2));
		await Wait.UntilAsync(() => h.Errors.Any());
		string key = h.Errors.First().GetProperty("key").GetString()!;

		release.TrySetResult();
		Assert.True(await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)));
		await Wait.UntilAsync(() => h.Transport.Events("notifications", "clear")
			.Any(cleared => cleared.GetProperty("key").GetString() == key));
	}

	[Fact]
	public async Task ALateCancellationKeepsItsNotice() {
		await using var h = SeverityHarness.Start();
		var entered = Signal();
		using var slow = h.Handle("slow", MessageActivity.ActionFailed("Posting"), async ct => {
			entered.TrySetResult();
			await Task.Delay(Timeout.Infinite, ct);
			return new Result(true);
		});
		using var next = h.Handle("next", MessageActivity.ActionFailed("Reading"), _ => Task.FromResult(new Result(true)));

		var running = h.Send("slow");
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		h.Time.Advance(SeverityPolicy.Deadline);
		await running.WaitAsync(TimeSpan.FromSeconds(2));
		await Wait.UntilAsync(() => h.Errors.Any());
		string key = h.Errors.First().GetProperty("key").GetString()!;

		await h.Send("next").WaitAsync(TimeSpan.FromSeconds(2));
		Assert.DoesNotContain(h.Transport.Events("notifications", "clear"), cleared => cleared.GetProperty("key").GetString() == key);
	}

	[Fact]
	public async Task TheDeadlineAlwaysCancelsAHandlerItGaveUpOn() {
		for (int attempt = 0; attempt < 200; attempt++) {
			await using var h = SeverityHarness.Start();
			TaskCompletionSource entered = Signal(), cancelled = Signal();
			using var slow = h.Handle("slow", MessageActivity.ActionFailed("Posting"), async ct => {
				using var registration = ct.Register(cancelled.SetResult);
				entered.TrySetResult();
				await cancelled.Task;
				return new Result(true);
			});

			var running = h.Send("slow");
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
			h.Time.Advance(SeverityPolicy.Deadline);
			await running.WaitAsync(TimeSpan.FromSeconds(2));
			await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
		}
	}

	[Fact]
	public async Task QuiesceFinishesWithAHandlerStuckForeverAndWorkQueuedBehindIt() {
		await using var h = SeverityHarness.Start();
		TaskCompletionSource entered = Signal(), never = Signal();
		using var stuck = h.Handle("stuck", MessageActivity.ActionFailed("Posting"), _ => Blocked(entered, never));

		var running = h.Send("stuck");
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		h.Time.Advance(SeverityPolicy.Deadline);
		await running.WaitAsync(TimeSpan.FromSeconds(2));
		var queued = h.Send("stuck");

		await h.Endpoint.Bus.QuiesceAsync().WaitAsync(TimeSpan.FromSeconds(2));
		await queued.WaitAsync(TimeSpan.FromSeconds(2));
	}

	[Fact]
	public async Task RepeatedFailuresShareOneNoticeWithACount() {
		await using var h = SeverityHarness.Start();
		TaskCompletionSource entered = Signal(), release = Signal();
		using var stuck = h.Handle("stuck", MessageActivity.ActionFailed("Posting"), _ => Blocked(entered, release));
		using var typed = h.Handle("typed", MessageActivity.ActionFailed("Typing"), _ => Task.FromResult(new Result(true)));

		var running = h.Send("stuck");
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		var queued = Enumerable.Range(0, 3).Select(_ => h.Send("typed")).ToArray();
		h.Time.Advance(SeverityPolicy.Deadline);
		await Task.WhenAll(queued.Append(running)).WaitAsync(TimeSpan.FromSeconds(2));

		await Wait.UntilAsync(() => h.Errors.Any(toast => toast.GetProperty("message").GetString()!.EndsWith("(3 times)", StringComparison.Ordinal)));
		Assert.Single(h.Errors.Where(toast => toast.GetProperty("message").GetString()!.Contains("Typing", StringComparison.Ordinal))
			.Select(toast => toast.GetProperty("key").GetString()).Distinct());
		release.TrySetResult();
	}

	[Fact]
	public async Task ANoDeadlineOperationKeepsRunningAndOffersCancel() {
		await using var h = SeverityHarness.Start();
		var entered = Signal();
		var cancelled = Signal();
		using var slow = h.Handle("slow", MessageActivity.NoDeadline("Creating a session"), async ct => {
			entered.TrySetResult();
			await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => cancelled.TrySetResult(), TaskScheduler.Default);
			return new Result(true);
		});

		var running = h.Send("slow");
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
		h.Time.Advance(SeverityPolicy.Deadline);
		await Wait.UntilAsync(() => h.Transport.Events("notifications", "show").Any(toast => toast.TryGetProperty("action", out _)));
		var notice = h.Transport.Events("notifications", "show").First(toast => toast.TryGetProperty("action", out _));
		Assert.Equal("Creating a session is still running after 0.1 seconds.", notice.GetProperty("message").GetString());
		Assert.False(running.IsCompleted);
		Assert.False(Assert.Single(h.Router.Health(ingressResponsive: true).ActiveOperations).Bounded);

		string id = JsonDocument.Parse(notice.GetProperty("action").GetProperty("argsJson").GetString()!).RootElement.GetProperty("id").GetString()!;
		Assert.True(h.Router.TryCancelOperation(id));
		await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
		await running.WaitAsync(TimeSpan.FromSeconds(2));
	}

	[Fact]
	public async Task SessionCriticalCannotBeDeclaredOnTheHostBus() {
		var transport = new RecordingTransport();
		await using var router = new HostMessageRouter(transport, new InlineUiDispatcher(), _ => { }, SeverityPolicy, new ManualTimeProvider());
		Assert.Throws<ArgumentException>(() => router.Host.Feature("work").Handle(
			"save", MessageActivity.SessionCritical("Saving"), OperationSupervisionJson.Default.Empty, OperationSupervisionJson.Default.Result,
			(_, _) => Task.FromResult(new Result(true))));
	}
}
