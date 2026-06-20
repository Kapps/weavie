using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="ClaudeResumeWatcher"/>: a create launch is confirmed on first output; a resume launch is
/// flagged failed when claude prints "No conversation found" (even split across chunks) and confirmed resumed
/// once it streams healthy output without that marker; and the watcher settles exactly once.
/// </summary>
public sealed class ClaudeResumeWatcherTests {
	[Fact]
	public void CreateLaunch_FirstOutput_IsCreated() {
		var watcher = new ClaudeResumeWatcher(resuming: false);

		Assert.Equal(ClaudeStartupOutcome.Created, watcher.Observe("[2J[H? for shortcuts"));
	}

	[Fact]
	public void ResumeLaunch_NotFoundMessage_IsResumeFailed() {
		var watcher = new ClaudeResumeWatcher(resuming: true);

		var outcome = watcher.Observe("No conversation found with session ID: e74af419-102e\n");

		Assert.Equal(ClaudeStartupOutcome.ResumeFailed, outcome);
	}

	[Fact]
	public void ResumeLaunch_NotFoundSplitAcrossChunks_IsResumeFailed() {
		var watcher = new ClaudeResumeWatcher(resuming: true);

		Assert.Equal(ClaudeStartupOutcome.Pending, watcher.Observe("No conversation "));
		Assert.Equal(ClaudeStartupOutcome.ResumeFailed, watcher.Observe("found with session ID: x"));
	}

	[Fact]
	public void ResumeLaunch_HealthyOutput_IsResumed() {
		var watcher = new ClaudeResumeWatcher(resuming: true);

		var outcome = ClaudeStartupOutcome.Pending;
		for (int i = 0; i < 10 && outcome == ClaudeStartupOutcome.Pending; i++) {
			outcome = watcher.Observe(new string('x', 1000));
		}

		Assert.Equal(ClaudeStartupOutcome.Resumed, outcome);
	}

	[Fact]
	public void SettlesOnce_ThenAlwaysPending() {
		var watcher = new ClaudeResumeWatcher(resuming: true);

		Assert.Equal(ClaudeStartupOutcome.ResumeFailed, watcher.Observe("No conversation found with session ID: x"));
		Assert.Equal(ClaudeStartupOutcome.Pending, watcher.Observe("No conversation found with session ID: x"));
	}
}
