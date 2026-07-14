using System.Collections.Concurrent;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class OrderedMessageQueueTests {
	[Fact]
	public async Task MixedProducers_DrainOnceInEnqueueOrder() {
		var scheduled = new ConcurrentQueue<Action>();
		List<string> sent = [];
		var queue = new OrderedMessageQueue(scheduled.Enqueue, sent.Add);

		await Task.Run(() => queue.Enqueue("background"));
		queue.Enqueue("ui");

		var drain = Assert.Single(scheduled);
		Assert.True(scheduled.TryDequeue(out _));
		drain();
		Assert.Equal(["background", "ui"], sent);
		Assert.Empty(scheduled);
	}

	[Fact]
	public async Task MessageQueuedWhileDraining_IsNotStranded() {
		var scheduled = new Queue<Action>();
		List<string> sent = [];
		using var sending = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var queue = new OrderedMessageQueue(scheduled.Enqueue, message => {
			sent.Add(message);
			if (message == "first") {
				sending.Set();
				release.Wait();
			}
		});

		queue.Enqueue("first");
		var drain = Task.Run(scheduled.Dequeue());
		sending.Wait();
		queue.Enqueue("second");
		release.Set();
		await drain;

		Assert.Equal(["first", "second"], sent);
		Assert.Empty(scheduled);
	}

	[Fact]
	public void Dispose_DropsScheduledAndFutureMessages() {
		var scheduled = new Queue<Action>();
		List<string> sent = [];
		var queue = new OrderedMessageQueue(scheduled.Enqueue, sent.Add);

		queue.Enqueue("scheduled-before-close");
		queue.Dispose();
		queue.Enqueue("posted-after-close");

		Assert.Single(scheduled);
		var drain = scheduled.Dequeue();
		drain();
		Assert.Empty(sent);
		Assert.Empty(scheduled);
	}

	[Fact]
	public async Task Dispose_WaitsForInFlightScheduler() {
		var scheduled = new Queue<Action>();
		List<string> sent = [];
		using var scheduling = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var queue = new OrderedMessageQueue(action => {
			scheduling.Set();
			release.Wait();
			scheduled.Enqueue(action);
		}, sent.Add);

		var enqueue = Task.Run(() => queue.Enqueue("during-close"));
		scheduling.Wait();
		Exception? disposeError = null;
		var dispose = new Thread(() => {
			try {
				queue.Dispose();
			} catch (Exception ex) {
				disposeError = ex;
			}
		});
		dispose.Start();
		bool blocked = WaitUntilBlockedOrStopped(dispose);
		release.Set();
		await enqueue;
		dispose.Join();

		Assert.True(blocked);
		Assert.Null(disposeError);
		Assert.Single(scheduled);
		scheduled.Dequeue()();
		Assert.Empty(sent);
	}

	[Fact]
	public async Task Dispose_WaitsForInFlightSend() {
		var scheduled = new Queue<Action>();
		List<string> sent = [];
		using var sending = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var queue = new OrderedMessageQueue(scheduled.Enqueue, message => {
			sending.Set();
			release.Wait();
			sent.Add(message);
		});

		queue.Enqueue("during-close");
		var drain = Task.Run(scheduled.Dequeue());
		sending.Wait();
		Exception? disposeError = null;
		var dispose = new Thread(() => {
			try {
				queue.Dispose();
			} catch (Exception ex) {
				disposeError = ex;
			}
		});
		dispose.Start();
		bool blocked = WaitUntilBlockedOrStopped(dispose);
		release.Set();
		await drain;
		dispose.Join();

		Assert.True(blocked);
		Assert.Null(disposeError);
		Assert.Equal(["during-close"], sent);
		queue.Enqueue("after-close");
		Assert.Empty(scheduled);
	}

	private static bool WaitUntilBlockedOrStopped(Thread thread) {
		var state = ThreadState.Unstarted;
		SpinWait.SpinUntil(() => {
			state = thread.ThreadState;
			return (state & (ThreadState.WaitSleepJoin | ThreadState.Stopped)) != 0;
		});
		return (state & ThreadState.WaitSleepJoin) != 0;
	}
}
