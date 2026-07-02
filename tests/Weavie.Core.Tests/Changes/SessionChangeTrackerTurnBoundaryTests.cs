using Weavie.Core.Changes;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// The turn-start boundary (UserPromptSubmit) commits the faded accepted band: kept hunks leave the diff view,
/// pending ones keep accumulating, and the undo history clears (a commit locks kept hunks in). See
/// <c>docs/specs/turn-review.md</c>.
/// </summary>
public sealed class SessionChangeTrackerTurnBoundaryTests {
	private static SessionChangeTracker Tracker(IFileSystem fileSystem) =>
		new(fileSystem, path => path.StartsWith("/w", StringComparison.Ordinal));

	private static readonly HookRequest NewPrompt = new() {
		Event = HookEventKind.UserPromptSubmit,
		ToolName = string.Empty,
		ToolInputJson = "{}",
	};

	// One tracked edit of /w/a.txt: content -> edited, with the tracker's baselines seeded from `content`.
	private static (InMemoryFileSystem FileSystem, SessionChangeTracker Tracker) Edited(string content, string edited) {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", content);
		var tracker = Tracker(fileSystem);
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", edited);
		tracker.RecordChange("/w/a.txt");
		return (fileSystem, tracker);
	}

	[Fact]
	public void NewPrompt_FullyKeptFile_LeavesTheReviewSet() {
		var (_, tracker) = Edited("a\nb\n", "a\nB\n");
		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(2, 3), new LineRange(2, 3), "B"));
		Assert.Single(tracker.TurnChanges()); // kept-but-uncommitted: the faded band holds the file in the set

		tracker.Observe(NewPrompt);

		Assert.Empty(tracker.TurnChanges()); // the boundary committed the band — the file drops from the diff view
		Assert.Single(tracker.Changes());    // the session diff (b -> B) survives
	}

	[Fact]
	public void NewPrompt_PartiallyKeptFile_ClearsOnlyTheFadedBand() {
		var (_, tracker) = Edited("a\nb\nc\nd\ne\n", "a\nB\nc\nD\ne\n"); // two hunks (lines 2 and 4)
		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(4, 5), new LineRange(4, 5), "D"));

		tracker.Observe(NewPrompt);

		var change = Assert.Single(tracker.TurnChanges());              // still pending: the unkept first hunk
		Assert.Equal(change.AcceptedBaselineText, change.BaselineText); // faded band collapsed (anchor caught up)
		Assert.Equal("a\nb\nc\nD\ne\n", change.BaselineText);           // review baseline held — hunk 1 stays pending
		Assert.Equal("a\nB\nc\nD\ne\n", change.CurrentText);
	}

	[Fact]
	public void NewPrompt_WithKeptHunks_ClearsUndoHistory() {
		var (_, tracker) = Edited("a\nb\n", "a\nB\n");
		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(2, 3), new LineRange(2, 3), "B"));
		Assert.True(tracker.CanUndoKeep);

		tracker.Observe(NewPrompt);

		// The commit locked the kept hunk in — undoing the keep would resurrect it via its stale anchor snapshot.
		Assert.False(tracker.CanUndo);
		Assert.False(tracker.CanRedo);
	}

	[Fact]
	public void NewPrompt_RaisesAcceptedCommittedWithThePaths() {
		var (_, tracker) = Edited("a\nb\n", "a\nB\n");
		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(2, 3), new LineRange(2, 3), "B"));
		IReadOnlyList<string>? committed = null;
		tracker.AcceptedCommitted += paths => committed = paths;

		tracker.Observe(NewPrompt);

		Assert.Equal(["/w/a.txt"], committed);
	}

	[Fact]
	public void NewPrompt_NothingKept_IsANoOp() {
		// Pending-only changes must survive the boundary untouched (the accumulate model), and a revert's undo
		// must not be lost to a boundary that had nothing to commit.
		var (_, tracker) = Edited("a\nb\nc\nd\ne\n", "a\nB\nc\nD\ne\n"); // two hunks (lines 2 and 4)
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertHunk("/w/a.txt", new LineRange(4, 5), new LineRange(4, 5), "D"));
		Assert.True(tracker.CanUndoRevert);
		int fired = 0;
		tracker.AcceptedCommitted += _ => fired++;

		tracker.Observe(NewPrompt);

		Assert.Equal(0, fired);              // nothing to commit — no re-push churn
		Assert.True(tracker.CanUndoRevert);  // the revert stays undoable across the boundary
		var change = Assert.Single(tracker.TurnChanges());
		Assert.Equal("a\nb\nc\nd\ne\n", change.BaselineText); // the unkept first hunk is still pending
	}
}
