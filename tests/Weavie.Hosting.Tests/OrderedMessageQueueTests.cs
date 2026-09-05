using System.Collections.Concurrent;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class OrderedMessageQueueTests {
	[Fact]
	public async Task MixedProducers_DrainOnceInEnqueueOrder() {
		var scheduled = new ConcurrentQueue<Action>();
		List<string> sent = [];
		var queue = new OrderedMessageQueue(scheduled.Enqueue, sent.Add, failure => Assert.Fail(failure.ToString()));

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
		}, failure => Assert.Fail(failure.ToString()));

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
		var queue = new OrderedMessageQueue(scheduled.Enqueue, sent.Add, failure => Assert.Fail(failure.ToString()));

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
		}, sent.Add, failure => Assert.Fail(failure.ToString()));

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
		}, failure => Assert.Fail(failure.ToString()));

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

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FailedTransport_ClosesBacklogAndReportsOnce(bool schedulingFails) {
		var scheduled = new Queue<Action>();
		var failures = new List<Exception>();
		var failure = new InvalidOperationException("transport failed");
		int sends = 0;
		var queue = new OrderedMessageQueue(action => {
			if (schedulingFails) {
				throw failure;
			}
			scheduled.Enqueue(action);
		}, _ => {
			sends++;
			throw failure;
		}, failures.Add);
		queue.Enqueue("first");
		queue.Enqueue("backlog");
		if (!schedulingFails) {
			scheduled.Dequeue()();
		}
		queue.Enqueue("after-failure");
		Assert.Same(failure, Assert.Single(failures));
		Assert.Equal(schedulingFails ? 0 : 1, sends);
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
