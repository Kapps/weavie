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
}
