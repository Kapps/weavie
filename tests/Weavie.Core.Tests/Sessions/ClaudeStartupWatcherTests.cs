using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="ClaudeStartupWatcher"/>: a launch is confirmed up only once it streams a full TUI's
/// worth of output, not on the first byte; confirmation settles once; and exit maps to the right recovery —
/// a clean exit or confirmed run heals nothing, an unconfirmed crash re-creates the same id when resuming or
/// forgets a poison create id.
/// </summary>
public sealed class ClaudeStartupWatcherTests {
	// The watcher's confirmation threshold; one chunk this size crosses it.
	private const int Confirmed = 4096;

	[Fact]
	public void ShortError_DoesNotConfirm() {
		// An error line under the threshold must not confirm, even though it is the first output.
		var watcher = new ClaudeStartupWatcher(resuming: false);

		Assert.False(watcher.Observe("No conversation found with session ID: e74af419\n"));
		Assert.False(watcher.Confirmed);
	}

	[Fact]
	public void EnoughOutput_Confirms() {
		var watcher = new ClaudeStartupWatcher(resuming: true);

		Assert.True(watcher.Observe(new string('x', Confirmed)));
		Assert.True(watcher.Confirmed);
	}

	[Fact]
	public void OutputAcrossChunks_ConfirmsOnceThresholdCrossed() {
		var watcher = new ClaudeStartupWatcher(resuming: false);

		Assert.False(watcher.Observe(new string('x', Confirmed - 1)));
		Assert.True(watcher.Observe("xx")); // crosses the threshold
		Assert.True(watcher.Confirmed);
	}

	[Fact]
	public void ConfirmSettlesOnce_ThenObserveIsFalse() {
		var watcher = new ClaudeStartupWatcher(resuming: true);

		Assert.True(watcher.Observe(new string('x', Confirmed)));
		Assert.False(watcher.Observe(new string('x', Confirmed))); // already confirmed — no second signal
	}

	[Fact]
	public void Exit_Confirmed_HealsNothing() {
		var watcher = new ClaudeStartupWatcher(resuming: true);
		watcher.Observe(new string('x', Confirmed));

		Assert.Equal(ClaudeStartupRecovery.None, watcher.OnExit(1)); // up, then crashed — its id is fine
	}

	[Fact]
	public void Exit_CleanWhileUnconfirmed_HealsNothing() {
		var watcher = new ClaudeStartupWatcher(resuming: true);

		Assert.Equal(ClaudeStartupRecovery.None, watcher.OnExit(0)); // code 0 = the user quit, not a failure
	}

	[Fact]
	public void Exit_ResumeCrashedAtStartup_RecreatesSameId() {
		var watcher = new ClaudeStartupWatcher(resuming: true);

		Assert.Equal(ClaudeStartupRecovery.RecreateSameId, watcher.OnExit(1));
	}

	[Fact]
	public void Exit_CreateCrashedAtStartup_ForgetsId() {
		var watcher = new ClaudeStartupWatcher(resuming: false);

		Assert.Equal(ClaudeStartupRecovery.ForgetId, watcher.OnExit(1));
	}
}
