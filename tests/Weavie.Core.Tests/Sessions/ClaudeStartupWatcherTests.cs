using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="ClaudeStartupWatcher"/>: a launch is confirmed up only once it streams a full TUI's
/// worth of output, not on the first byte; confirmation settles once; and only an unconfirmed non-zero exit
/// counts as a failure to start (a clean exit or a confirmed run does not), so a session id that could not be
/// brought up is forgotten while a healthy one is left alone.
/// </summary>
public sealed class ClaudeStartupWatcherTests {
	// The watcher's confirmation threshold; one chunk this size crosses it.
	private const int Confirmed = 4096;

	[Fact]
	public void ShortError_DoesNotConfirm() {
		// An error line under the threshold must not confirm, even though it is the first output.
		var watcher = new ClaudeStartupWatcher();

		Assert.False(watcher.Observe("No conversation found with session ID: e74af419\n"));
		Assert.False(watcher.Confirmed);
	}

	[Fact]
	public void EnoughOutput_Confirms() {
		var watcher = new ClaudeStartupWatcher();

		Assert.True(watcher.Observe(new string('x', Confirmed)));
		Assert.True(watcher.Confirmed);
	}

	[Fact]
	public void OutputAcrossChunks_ConfirmsOnceThresholdCrossed() {
		var watcher = new ClaudeStartupWatcher();

		Assert.False(watcher.Observe(new string('x', Confirmed - 1)));
		Assert.True(watcher.Observe("xx")); // crosses the threshold
		Assert.True(watcher.Confirmed);
	}

	[Fact]
	public void ConfirmSettlesOnce_ThenObserveIsFalse() {
		var watcher = new ClaudeStartupWatcher();

		Assert.True(watcher.Observe(new string('x', Confirmed)));
		Assert.False(watcher.Observe(new string('x', Confirmed))); // already confirmed — no second signal
	}

	[Fact]
	public void Exit_Confirmed_DidNotFailToStart() {
		var watcher = new ClaudeStartupWatcher();
		watcher.Observe(new string('x', Confirmed));

		Assert.False(watcher.FailedToStart(1)); // up, then crashed — its id is fine
	}

	[Fact]
	public void Exit_CleanWhileUnconfirmed_DidNotFailToStart() {
		var watcher = new ClaudeStartupWatcher();

		Assert.False(watcher.FailedToStart(0)); // code 0 = the user quit, not a failure
	}

	[Fact]
	public void Exit_CrashedAtStartup_FailedToStart() {
		var watcher = new ClaudeStartupWatcher();

		Assert.True(watcher.FailedToStart(1)); // never came up: forget the id and mint fresh next launch
	}
}
