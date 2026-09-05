using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class GuardedUiDispatcherTests {
	[Fact]
	public void FailedPostReportsOnceAndDoesNotStopLaterWork() {
		var failures = new List<Exception>();
		var dispatcher = new GuardedUiDispatcher(new InlineUiDispatcher(), failures.Add);
		var failure = new InvalidOperationException("action failed");
		dispatcher.Post(() => throw failure);
		bool ran = false;
		dispatcher.Post(() => ran = true);
		Assert.Same(failure, Assert.Single(failures));
		Assert.True(ran);
	}

	[Fact]
	public async Task AwaitedFailureStillReachesItsCaller() {
		var failures = new List<Exception>();
		var dispatcher = new GuardedUiDispatcher(new InlineUiDispatcher(), failures.Add);
		await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(
			() => throw new InvalidOperationException("awaited"), CancellationToken.None));
		Assert.Empty(failures);
	}

	[Fact]
	public void ReporterFailureReachesOuterShellGuard() {
		var failures = new List<Exception>();
		var shell = new GuardedUiDispatcher(new InlineUiDispatcher(), failures.Add);
		var dispatcher = new GuardedUiDispatcher(shell, _ => throw new InvalidOperationException("report failed"));
		dispatcher.Post(() => throw new InvalidOperationException("action failed"));
		var failure = Assert.IsType<AggregateException>(Assert.Single(failures));
		Assert.Equal(["action failed", "report failed"], failure.InnerExceptions.Select(error => error.Message));
	}
}
