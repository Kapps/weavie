using System.Collections.Concurrent;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class DeferredUiQueueTests {
	[Fact]
	public async Task Queued_actions_run_after_the_UI_handler_that_queued_them_returns_and_in_order() {
		using var ui = new InlineOnUiThreadDispatcher();
		var queue = new DeferredUiQueue(ui);
		var events = new ConcurrentQueue<string>();
		var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		ui.Post(() => {
			queue.Enqueue(() => events.Enqueue("first"));
			queue.Enqueue(() => {
				events.Enqueue("second");
				done.SetResult();
			});
			events.Enqueue("handler returned");
		});
		await done.Task.WaitAsync(TimeSpan.FromSeconds(5));

		Assert.Equal(["handler returned", "first", "second"], events);
		Assert.True(ui.RanEverythingOnItsThread);
	}

	[Fact]
	public async Task Closing_drops_actions_that_have_not_started() {
		var ui = new ManualUiDispatcher(paused: true);
		var queue = new DeferredUiQueue(ui);
		bool ran = false;

		queue.Enqueue(() => ran = true);
		await ui.WaitForPostAsync().WaitAsync(TimeSpan.FromSeconds(5));
		queue.Close();
		ui.RunPending();

		Assert.False(ran);
	}

	// Like the WinForms and Cocoa dispatchers: inline when already on the UI thread, queued from anywhere else.
	private sealed class InlineOnUiThreadDispatcher : IUiDispatcher, IDisposable {
		private readonly BlockingCollection<Action> _work = [];
		private readonly Thread _thread;
		private volatile bool _offThread;

		public InlineOnUiThreadDispatcher() {
			_thread = new Thread(() => {
				foreach (var action in _work.GetConsumingEnumerable()) {
					action();
				}
			}) { IsBackground = true };
			_thread.Start();
		}

		public bool RanEverythingOnItsThread => !_offThread;

		public void Post(Action action) {
			if (Thread.CurrentThread == _thread) {
				action();
			} else {
				_work.Add(() => {
					_offThread |= Thread.CurrentThread != _thread;
					action();
				});
			}
		}

		public void Dispose() => _work.CompleteAdding();
	}
}
