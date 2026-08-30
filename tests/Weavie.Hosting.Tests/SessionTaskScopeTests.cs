using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class SessionTaskScopeTests {
	[Fact]
	public async Task WorkIsTrackedBeforeItsCodeCanDisposeTheScope() {
		await using var scope = new SessionTaskScope(_ => { });
		ValueTask disposal = default;
		bool completedInsideWork = true;

		var running = scope.Run(_ => {
			disposal = scope.DisposeAsync();
			completedInsideWork = disposal.IsCompleted;
			return Task.CompletedTask;
		});

		await running!;
		await disposal;
		Assert.False(completedInsideWork);
	}

	[Fact]
	public async Task DisposalCancelsAndDrainsOwnedWorkAndRejectsLaterStarts() {
		var errors = new List<string>();
		var scope = new SessionTaskScope(errors.Add);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var running = scope.Run(async ct => {
			entered.SetResult();
			try {
				await Task.Delay(Timeout.InfiniteTimeSpan, ct);
			} catch (OperationCanceledException) when (ct.IsCancellationRequested) {
				cancelled.SetResult();
				throw;
			}
		});
		await entered.Task;

		var firstDispose = scope.DisposeAsync().AsTask();
		var secondDispose = scope.DisposeAsync().AsTask();
		await Task.WhenAll(firstDispose, secondDispose);
		await running!;
		int lateCalls = 0;
		var late = scope.Run(_ => {
			lateCalls++;
			return Task.CompletedTask;
		});

		Assert.True(scope.Stopping.IsCancellationRequested);
		Assert.True(cancelled.Task.IsCompletedSuccessfully);
		Assert.Null(late); // a closed scope admits nothing, so the caller can release what it held
		Assert.Equal(0, lateCalls);
		Assert.Empty(errors);
	}
}
