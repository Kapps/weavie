using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class OrderedAfterResponseTests {
	[Fact]
	public async Task RapidSaveCompletions_RunInWriteOrderAndReleaseAfterFailure() {
		var order = new List<int>();
		var completions = new OrderedAfterResponse();
		var first = completions.Reserve(_ => {
			order.Add(1);
			return Task.FromException(new InvalidOperationException("first failed"));
		});
		var second = completions.Reserve(_ => {
			order.Add(2);
			return Task.CompletedTask;
		});

		var secondRun = second(CancellationToken.None);
		Assert.False(secondRun.IsCompleted);
		Assert.Empty(order);

		await Assert.ThrowsAsync<InvalidOperationException>(() => first(CancellationToken.None));
		await secondRun;
		Assert.Equal([1, 2], order);
	}
}
