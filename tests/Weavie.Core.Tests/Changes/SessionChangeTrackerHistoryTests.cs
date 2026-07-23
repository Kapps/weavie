using Weavie.Core.Changes;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Undo/redo history over review actions: keep/revert at hunk/file/all, reversed or re-applied.</summary>
public sealed class SessionChangeTrackerHistoryTests {
	private static SessionChangeTracker Tracker(IFileSystem fileSystem) =>
		new(fileSystem, "/w", path => path.StartsWith("/w", StringComparison.Ordinal));

	// Records a single-hunk change (baseline `from` -> current `to`) on a freshly-baselined file.
	private static SessionChangeTracker Changed(InMemoryFileSystem fileSystem, string path, string from, string to) {
		fileSystem.WriteAllText(path, from);
		var tracker = Tracker(fileSystem);
		tracker.CaptureBaseline(path);
		fileSystem.WriteAllText(path, to);
		tracker.RecordChange(path);
		return tracker;
	}

	[Fact]
	public void UndoLastKeep_RestoresPendingHunk() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Changed(fileSystem, "/w/a.txt", "a\nb\n", "a\nB\n");
		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(2, 3), new LineRange(2, 3), "B"));
		Assert.Equal("a\nB\n", tracker.GetTurn("/w/a.txt")!.BaselineText); // kept → review baseline == current (no pending hunk)
		Assert.True(tracker.CanUndoKeep);

		var result = tracker.UndoLastKeep();

		Assert.True(result.Acted);
		Assert.False(result.TouchedDisk); // keep + its undo never write disk
		var change = tracker.GetTurn("/w/a.txt");
		Assert.NotNull(change);
		Assert.Equal("a\nb\n", change!.BaselineText); // baseline rolled back, hunk pending again
		Assert.Equal("a\nB\n", change.CurrentText);
		Assert.Single(tracker.TurnChanges());
		Assert.False(tracker.CanUndoKeep);
		Assert.True(tracker.CanRedo);
	}

	[Fact]
	public void Redo_AfterUndoKeep_ReappliesIt() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Changed(fileSystem, "/w/a.txt", "a\nb\n", "a\nB\n");
		tracker.KeepHunk("/w/a.txt", new LineRange(2, 3), new LineRange(2, 3), "B");
		tracker.UndoLastKeep();

		var result = tracker.Redo();

		Assert.True(result.Acted);
		Assert.Equal("a\nB\n", tracker.GetTurn("/w/a.txt")!.BaselineText); // kept again → review baseline == current
		Assert.False(tracker.CanRedo);
		Assert.True(tracker.CanUndoKeep);
	}

	[Fact]
	public void UndoLastRevert_RestoresChangeOnDisk() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Changed(fileSystem, "/w/a.txt", "a\nb\n", "a\nB\n");
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertHunk("/w/a.txt", new LineRange(2, 3), new LineRange(2, 3), "B"));
		Assert.Equal("a\nb\n", fileSystem.ReadAllText("/w/a.txt")); // reverted on disk
		Assert.True(tracker.CanUndoRevert);

		var result = tracker.UndoLastRevert();

		Assert.True(result.Acted);
		Assert.True(result.TouchedDisk);
		Assert.Equal("a\nB\n", fileSystem.ReadAllText("/w/a.txt")); // change rewritten to disk
		var change = tracker.GetTurn("/w/a.txt");
		Assert.NotNull(change);
		Assert.Equal("a\nB\n", change!.CurrentText); // pending again
		Assert.True(tracker.CanRedo);
	}

	[Fact]
	public void UndoLastRevert_OfDeletedCreatedFile_RecreatesIt() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Tracker(fileSystem);
		tracker.CaptureBaseline("/w/new.txt"); // absent at baseline
		fileSystem.WriteAllText("/w/new.txt", "hello\nworld\n");
		tracker.RecordChange("/w/new.txt");
		Assert.Equal(RevertHunkOutcome.Deleted, tracker.RevertHunk("/w/new.txt", new LineRange(1, 1), new LineRange(1, 4), "hello\nworld\n"));
		Assert.False(fileSystem.FileExists("/w/new.txt"));

		var result = tracker.UndoLastRevert();

		Assert.True(result.Acted);
		Assert.True(fileSystem.FileExists("/w/new.txt")); // re-created from the snapshot
		Assert.Equal("hello\nworld\n", fileSystem.ReadAllText("/w/new.txt"));
		Assert.Single(tracker.TurnChanges()); // back in the review set as a created file
	}

	[Fact]
	public void UndoRedo_OfHunkAction_CarriesTheActedLine() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Changed(fileSystem, "/w/a.txt", "a\nb\nc\nd\n", "a\nb\nc\nD\n");
		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(4, 5), new LineRange(4, 5), "D"));

		// The undo/redo name the hunk's current-side line, so the host lands on it — not the file's first hunk.
		Assert.Equal(4, tracker.UndoLastKeep().Line);
		Assert.Equal(4, tracker.Redo().Line);

		tracker.UndoLastKeep();
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertHunk("/w/a.txt", new LineRange(4, 5), new LineRange(4, 5), "D"));
		Assert.Equal(4, tracker.UndoLastRevert().Line);
	}

	[Fact]
	public void UndoRedo_OfFileScopeAction_CarriesNoLine() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Changed(fileSystem, "/w/a.txt", "a\nb\n", "a\nB\n");
		tracker.KeepFile("/w/a.txt");

		Assert.Null(tracker.UndoLastKeep().Line);

		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertFile("/w/a.txt"));
		Assert.Null(tracker.UndoLastRevert().Line);
	}

	[Fact]
	public void TypeSplit_UndoKeepIgnoresRevert_AndViceVersa() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a\n");
		fileSystem.WriteAllText("/w/b.txt", "b\n");
		var tracker = Tracker(fileSystem);
		tracker.CaptureBaseline("/w/a.txt");
		tracker.CaptureBaseline("/w/b.txt");
		fileSystem.WriteAllText("/w/a.txt", "A\n");
		fileSystem.WriteAllText("/w/b.txt", "B\n");
		tracker.RecordChange("/w/a.txt");
		tracker.RecordChange("/w/b.txt");

		tracker.KeepHunk("/w/a.txt", new LineRange(1, 2), new LineRange(1, 2), "A");   // a kept
		tracker.RevertHunk("/w/b.txt", new LineRange(1, 2), new LineRange(1, 2), "B"); // b reverted

		// Undo-keep reverses a's keep (not b's revert): a is pending again, b stays reverted on disk.
		Assert.True(tracker.UndoLastKeep().Acted);
		Assert.Equal("b\n", fileSystem.ReadAllText("/w/b.txt"));
		Assert.NotNull(tracker.GetTurn("/w/a.txt"));

		// Undo-revert reverses b's revert: b's change is back on disk.
		Assert.True(tracker.UndoLastRevert().Acted);
		Assert.Equal("B\n", fileSystem.ReadAllText("/w/b.txt"));
	}

	[Fact]
	public void Undo_BlockedByNewerEditToSamePath_DoesNotClobber() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Changed(fileSystem, "/w/a.txt", "a\nb\n", "a\nB\n");
		tracker.KeepHunk("/w/a.txt", new LineRange(2, 3), new LineRange(2, 3), "B");

		// A newer edit lands on the same file after the keep.
		fileSystem.WriteAllText("/w/a.txt", "a\nB\nc\n");
		tracker.RecordChange("/w/a.txt");

		var result = tracker.UndoLastKeep();

		Assert.False(result.Acted);
		Assert.True(result.WasBlocked); // a newer edit is in the way, not "nothing to undo"
	}

	[Fact]
	public void AcceptTurn_IsTheCommitPoint_ClearsHistory() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Changed(fileSystem, "/w/a.txt", "a\n", "A\n");
		tracker.KeepHunk("/w/a.txt", new LineRange(1, 2), new LineRange(1, 2), "A");
		Assert.True(tracker.CanUndoKeep);

		tracker.AcceptTurn();

		Assert.False(tracker.CanUndoKeep);
		Assert.False(tracker.CanRedo);
		Assert.False(tracker.UndoLastKeep().Acted); // nothing left to undo past the commit
	}

	[Fact]
	public void RevertAll_IsOneUndoStep_RestoringEveryFile() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a\n");
		fileSystem.WriteAllText("/w/b.txt", "b\n");
		var tracker = Tracker(fileSystem);
		tracker.CaptureBaseline("/w/a.txt");
		tracker.CaptureBaseline("/w/b.txt");
		fileSystem.WriteAllText("/w/a.txt", "A\n");
		fileSystem.WriteAllText("/w/b.txt", "B\n");
		tracker.RecordChange("/w/a.txt");
		tracker.RecordChange("/w/b.txt");

		var reverted = tracker.RevertAll();

		Assert.True(reverted.Acted);
		Assert.Equal("a\n", fileSystem.ReadAllText("/w/a.txt"));
		Assert.Equal("b\n", fileSystem.ReadAllText("/w/b.txt"));
		Assert.Empty(tracker.TurnChanges());

		// A single undo restores both files at once.
		Assert.True(tracker.UndoLastRevert().Acted);
		Assert.Equal("A\n", fileSystem.ReadAllText("/w/a.txt"));
		Assert.Equal("B\n", fileSystem.ReadAllText("/w/b.txt"));
		Assert.Equal(2, tracker.TurnChanges().Count);
	}

	[Fact]
	public void NewAction_AfterUndo_InvalidatesRedo() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Changed(fileSystem, "/w/a.txt", "a\nb\n", "A\nB\n"); // two hunks
		tracker.KeepHunk("/w/a.txt", new LineRange(1, 2), new LineRange(1, 2), "A"); // keep hunk 1
		tracker.UndoLastKeep();
		Assert.True(tracker.CanRedo);

		// A fresh keep after an undo drops the redo branch (standard linear history).
		tracker.KeepHunk("/w/a.txt", new LineRange(2, 3), new LineRange(2, 3), "B");

		Assert.False(tracker.CanRedo);
	}
}
