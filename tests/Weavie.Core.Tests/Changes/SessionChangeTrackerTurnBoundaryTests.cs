using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Prompt boundaries preserve decisions, pending changes, and their undo history.</summary>
public sealed class SessionChangeTrackerTurnBoundaryTests {
	private static SessionChangeTracker Tracker(IFileSystem fileSystem) =>
		new(fileSystem, NoopFileActivitySink.Instance, "/w", path => path.StartsWith("/w", StringComparison.Ordinal));

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
	public void NewPrompt_FullyKeptFile_RemainsReviewed() {
		var (_, tracker) = Edited("a\nb\n", "a\nB\n");
		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(2, 3), new LineRange(2, 3), "B"));
		Assert.Single(tracker.TurnChanges());

		tracker.Observe(NewPrompt);

		var change = Assert.Single(tracker.TurnChanges());
		Assert.Equal("a\nb\n", change.AcceptedBaselineText);
		Assert.Equal("a\nB\n", change.BaselineText);
		Assert.Single(tracker.Changes());
	}

	[Fact]
	public void NewPrompt_PartiallyKeptFile_PreservesBothBands() {
		var (_, tracker) = Edited("a\nb\nc\nd\ne\n", "a\nB\nc\nD\ne\n"); // two hunks (lines 2 and 4)
		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(4, 5), new LineRange(4, 5), "D"));

		tracker.Observe(NewPrompt);

		var change = Assert.Single(tracker.TurnChanges());
		Assert.Equal("a\nb\nc\nd\ne\n", change.AcceptedBaselineText);
		Assert.Equal("a\nb\nc\nD\ne\n", change.BaselineText);
		Assert.Equal("a\nB\nc\nD\ne\n", change.CurrentText);
	}

	[Fact]
	public void NewPrompt_WithKeptHunks_PreservesUndoHistory() {
		var (_, tracker) = Edited("a\nb\n", "a\nB\n");
		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(2, 3), new LineRange(2, 3), "B"));
		Assert.True(tracker.CanUndoKeep);

		tracker.Observe(NewPrompt);

		Assert.True(tracker.CanUndo);
		Assert.False(tracker.CanRedo);
		Assert.True(tracker.UndoLastKeep().Acted);
		Assert.Equal("a\nb\n", tracker.GetTurn("/w/a.txt")!.BaselineText);
	}

	[Fact]
	public void NewPrompt_PreservesRedoHistory() {
		var (_, tracker) = Edited("a\nb\n", "a\nB\n");
		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(2, 3), new LineRange(2, 3), "B"));
		Assert.True(tracker.UndoLastKeep().Acted);

		tracker.Observe(NewPrompt);

		Assert.True(tracker.CanRedo);
		Assert.True(tracker.Redo().Acted);
		Assert.Equal("a\nB\n", tracker.GetTurn("/w/a.txt")!.BaselineText);
	}

	[Fact]
	public void NewPrompt_UnkeepUsesTheUnchangedAcceptedAnchor() {
		var (_, tracker) = Edited("a\nb\nc\nd\ne\n", "n1\nn2\na\nb\nc\nD\ne\n"); // insert n1,n2 at top; d→D
		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(1, 1), new LineRange(1, 3), "n1\nn2"));
		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(6, 7), new LineRange(6, 7), "D"));

		tracker.Observe(NewPrompt);

		Assert.True(tracker.UnkeepHunk("/w/a.txt", new LineRange(4, 5), new LineRange(6, 7), "d", "D"));
		Assert.Equal("n1\nn2\na\nb\nc\nd\ne\n", tracker.GetTurn("/w/a.txt")!.BaselineText);
	}

	[Fact]
	public void NewPrompt_NothingKept_IsANoOp() {
		// Pending-only changes must survive the boundary untouched (the accumulate model), and a revert's undo
		// must not be lost to a boundary that had nothing to commit.
		var (_, tracker) = Edited("a\nb\nc\nd\ne\n", "a\nB\nc\nD\ne\n"); // two hunks (lines 2 and 4)
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertHunk("/w/a.txt", new LineRange(4, 5), new LineRange(4, 5), "D"));
		Assert.True(tracker.CanUndoRevert);

		tracker.Observe(NewPrompt);

		Assert.True(tracker.CanUndoRevert);  // the revert stays undoable across the boundary
		var change = Assert.Single(tracker.TurnChanges());
		Assert.Equal("a\nb\nc\nd\ne\n", change.BaselineText); // the unkept first hunk is still pending
	}
}
