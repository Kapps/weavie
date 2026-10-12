using System.Diagnostics;
using Weavie.Core.Processes;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The graceful-stop escalation: ask, wait for the exit until the deadline, then force-kill exactly once.</summary>
public sealed class ProcessStopTests {
	private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

	[Fact]
	public async Task ChildExitingOnTheAsk_IsGraceful() {
		var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int forced = 0;

		var outcome = await ProcessStop.RunAsync(
			Plan(_ => {
				exited.SetResult();
				return Task.CompletedTask;
			}),
			exited.Task,
			() => Interlocked.Increment(ref forced),
			new FakeSupervisorClock());

		Assert.Same(ProcessStopOutcome.Graceful, outcome);
		Assert.Equal(0, forced);
	}

	[Fact]
	public async Task ChildIgnoringTheAsk_IsForcedExactlyOncePastTheDeadline() {
		var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var clock = new FakeSupervisorClock();
		int forced = 0;

		var stopping = ProcessStop.RunAsync(Plan(_ => Task.CompletedTask), exited.Task, () => {
			Interlocked.Increment(ref forced);
			exited.TrySetResult();
		}, clock);
		clock.Advance(Deadline - TimeSpan.FromMilliseconds(1));
		await Task.Delay(50);
		Assert.Equal(0, forced);
		clock.Advance(TimeSpan.FromMilliseconds(1));

		var outcome = await stopping.WaitAsync(TimeSpan.FromSeconds(2));
		Assert.Equal("didn't exit within 5s of asking", outcome.ForcedBecause);
		Assert.Equal(1, forced);
	}

	[Fact]
	public async Task AskFindingTheChildGone_StillWaitsForItsExit() {
		var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int forced = 0;

		var stopping = ProcessStop.RunAsync(
			Plan(_ => throw new IOException("Broken pipe")),
			exited.Task,
			() => Interlocked.Increment(ref forced),
			new FakeSupervisorClock());
		await Task.Delay(50);
		Assert.False(stopping.IsCompleted);
		exited.SetResult();

		Assert.Same(ProcessStopOutcome.Graceful, await stopping.WaitAsync(TimeSpan.FromSeconds(2)));
		Assert.Equal(0, forced);
	}

	[Fact]
	public async Task AskThatFails_ForcesWithoutWaitingForTheDeadline() {
		var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

		var outcome = await ProcessStop.RunAsync(
			Plan(_ => throw new InvalidOperationException("connection refused")),
			exited.Task,
			exited.SetResult,
			new FakeSupervisorClock()).WaitAsync(TimeSpan.FromSeconds(2));

		Assert.Equal("couldn't be asked to stop (asking failed: connection refused)", outcome.ForcedBecause);
	}

	[Fact]
	public async Task RealChildExitingOnStdinEof_IsGraceful() {
		if (OperatingSystem.IsWindows()) return;
		var child = OwnedProcess.Start(Redirected("/bin/cat"));
		try {
			var outcome = await child.StopAsync(ProcessStopPlan.CloseInput(child, Deadline), new SystemSupervisorClock());
			Assert.Same(ProcessStopOutcome.Graceful, outcome);
		} finally {
			child.Dispose();
		}
	}

	[Fact]
	public async Task RealChildIgnoringStdinEof_IsForced() {
		if (OperatingSystem.IsWindows()) return;
		var info = Redirected("/bin/sleep");
		info.ArgumentList.Add("30");
		var child = OwnedProcess.Start(info);
		try {
			var outcome = await child.StopAsync(
				ProcessStopPlan.CloseInput(child, TimeSpan.FromMilliseconds(200)), new SystemSupervisorClock());
			Assert.Equal("didn't exit within 0.2s of closing its input", outcome.ForcedBecause);
			Assert.True(child.HasExited);
		} finally {
			child.Dispose();
		}
	}

	private static ProcessStopPlan Plan(Func<CancellationToken, Task> ask) => ProcessStopPlan.Request("asking", ask, Deadline);

	private static ProcessStartInfo Redirected(string command) => new(command) {
		RedirectStandardInput = true,
		RedirectStandardOutput = true,
		RedirectStandardError = true,
		UseShellExecute = false,
	};
}
