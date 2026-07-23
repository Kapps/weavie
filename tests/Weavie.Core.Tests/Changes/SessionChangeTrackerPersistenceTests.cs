using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class SessionChangeTrackerPersistenceTests {
	private const string Root = "/w";

	[Fact]
	public void PendingReview_RoundTripsByteExactMixedLineEndings() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/mixed.txt";
		const string baseline = "one\r\ntwo\rthree\n";
		const string current = "one\r\nTWO\rthree\nfour";
		fileSystem.WriteAllText(path, baseline);
		var tracker = Tracker(fileSystem, store);
		tracker.CaptureBaseline(path);
		fileSystem.WriteAllText(path, current);
		tracker.RecordChange(path);
		var expected = Assert.IsType<FileChange>(tracker.GetTurn(path));

		var restored = Tracker(fileSystem, store);

		var change = Assert.IsType<FileChange>(restored.GetTurn(path));
		Assert.Equal(expected.AcceptedBaselineText, change.AcceptedBaselineText);
		Assert.Equal(expected.BaselineText, change.BaselineText);
		Assert.Equal(expected.CurrentText, change.CurrentText);
		Assert.False(restored.CanUndo);
	}

	[Fact]
	public void AcceptedAndPendingHunks_RoundTripAsTheSameThreeVersions() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		fileSystem.WriteAllText(path, "one\ntwo\nthree");
		var tracker = Tracker(fileSystem, store);
		tracker.CaptureBaseline(path);
		fileSystem.WriteAllText(path, "ONE\ntwo\nthree");
		tracker.RecordChange(path);
		Assert.True(tracker.KeepHunk(path, new LineRange(1, 2), new LineRange(1, 2), "ONE"));
		tracker.CaptureBaseline(path);
		fileSystem.WriteAllText(path, "ONE\ntwo\nTHREE");
		tracker.RecordChange(path);

		var change = Assert.IsType<FileChange>(Tracker(fileSystem, store).GetTurn(path));

		Assert.Equal("one\ntwo\nthree", change.AcceptedBaselineText);
		Assert.Equal("ONE\ntwo\nthree", change.BaselineText);
		Assert.Equal("ONE\ntwo\nTHREE", change.CurrentText);
	}

	[Fact]
	public void RestoredReject_PreservesAnUnrelatedSavedUserEdit() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		fileSystem.WriteAllText(path, "a\nb\nc");
		var tracker = Tracker(fileSystem, store);
		tracker.CaptureBaseline(path);
		fileSystem.WriteAllText(path, "a\nB\nc");
		tracker.RecordChange(path);
		int saveCount = store.SaveCount;
		fileSystem.WriteAllText(path, "a\nB\nc\nuser note");
		tracker.RecordHandEdit(path, "a\nB\nc\nuser note");
		Assert.Equal(saveCount + 1, store.SaveCount);

		var restored = Tracker(fileSystem, store);
		Assert.Equal(RevertHunkOutcome.Reverted, restored.RevertFile(path));

		Assert.Equal("a\nb\nc\nuser note", fileSystem.ReadAllText(path));
	}

	[Fact]
	public void ChangedDisk_InvalidatesOnlyThatFile() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		var tracker = Tracker(fileSystem, store);
		Change(fileSystem, tracker, "/w/a.txt", "a", "A");
		Change(fileSystem, tracker, "/w/b.txt", "b", "B");
		fileSystem.WriteAllText("/w/a.txt", "externally changed");

		var restored = Tracker(fileSystem, store);

		Assert.Null(restored.GetTurn("/w/a.txt"));
		Assert.NotNull(restored.GetTurn("/w/b.txt"));
		Assert.Contains(restored.ReviewProblems, problem => problem.Path == "a.txt");
	}

	[Fact]
	public void InvalidatedFile_DoesNotResurrectWhenDiskLaterReturnsToTheOldAnchor() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		Change(fileSystem, Tracker(fileSystem, store), path, "old", "reviewed");
		fileSystem.WriteAllText(path, "external");

		var invalidated = Tracker(fileSystem, store);
		Assert.Null(invalidated.GetTurn(path));
		fileSystem.WriteAllText(path, "reviewed");

		var reopened = Tracker(fileSystem, store);

		Assert.Null(reopened.GetTurn(path));
	}

	[Fact]
	public void RestoredCreatedFile_IsDeletedWhenRejected() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/new.txt";
		var tracker = Tracker(fileSystem, store);
		tracker.CaptureBaseline(path);
		fileSystem.WriteAllText(path, "new");
		tracker.RecordChange(path);

		var restored = Tracker(fileSystem, store);
		Assert.Equal(RevertHunkOutcome.Deleted, restored.RevertFile(path));

		Assert.False(fileSystem.FileExists(path));
	}

	[Fact]
	public void RestoredProvenance_ContinuesWithANewOriginId() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		fileSystem.WriteAllText(path, "a\nb");
		var tracker = Tracker(fileSystem, store);
		tracker.CaptureBaseline(path);
		fileSystem.WriteAllText(path, "A\nb");
		tracker.RecordChange(path);
		long first = 0;
		tracker.Corrected += edits => first = edits[0].OriginId;
		fileSystem.WriteAllText(path, "AA\nb");
		tracker.RecordHandEdit(path, "AA\nb");

		var restored = Tracker(fileSystem, store);
		restored.CaptureBaseline(path);
		fileSystem.WriteAllText(path, "AA\nB");
		restored.RecordChange(path);
		long second = 0;
		restored.Corrected += edits => second = edits[0].OriginId;
		fileSystem.WriteAllText(path, "AA\nBB");
		restored.RecordHandEdit(path, "AA\nBB");

		Assert.True(first > 0);
		Assert.True(second > first);
	}

	[Fact]
	public void OriginCounter_SurvivesWhenAnArmedPrHasNoRemainingProvenance() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		fileSystem.WriteAllText(path, "a");
		var tracker = Tracker(fileSystem, store);
		long token = tracker.BeginReviewArm();
		Assert.True(tracker.ArmReview(
			token,
			new ReviewIdentity(1, "PR #1", "head", "base", "sha", null, Root),
			[]));
		tracker.CaptureBaseline(path);
		fileSystem.WriteAllText(path, "A");
		tracker.RecordChange(path);
		tracker.AcceptTurn();

		var restored = Tracker(fileSystem, store);
		restored.CaptureBaseline(path);
		fileSystem.WriteAllText(path, "B");
		restored.RecordChange(path);
		long origin = 0;
		restored.Corrected += edits => origin = edits[0].OriginId;
		fileSystem.WriteAllText(path, "BB");
		restored.RecordHandEdit(path, "BB");

		Assert.Equal(2, origin);
	}

	[Fact]
	public void FailedDiskNeutralCheckpoint_RollsBackTheReviewDecision() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		Change(fileSystem, Tracker(fileSystem, store), path, "old", "new");
		var tracker = Tracker(fileSystem, store);
		store.FailNextSave = true;

		Assert.Throws<ReviewPersistenceException>(() =>
			tracker.KeepHunk(path, new LineRange(1, 2), new LineRange(1, 2), "new"));

		var change = Assert.IsType<FileChange>(tracker.GetTurn(path));
		Assert.Equal("old", change.BaselineText);
		Assert.Equal("new", change.CurrentText);

		Assert.True(tracker.KeepHunk(path, new LineRange(1, 2), new LineRange(1, 2), "new"));
		Assert.Equal("new", Assert.IsType<FileChange>(Tracker(fileSystem, store).GetTurn(path)).BaselineText);
	}

	[Fact]
	public void FailedDiskChangingCheckpoint_IsRetriedWithTheNextReviewMutation() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string revertedPath = "/w/reverted.txt";
		const string keptPath = "/w/kept.txt";
		var tracker = Tracker(fileSystem, store);
		Change(fileSystem, tracker, revertedPath, "old", "new");
		Change(fileSystem, tracker, keptPath, "base", "kept");
		store.FailNextSave = true;

		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertFile(revertedPath));
		Assert.True(tracker.KeepHunk(keptPath, new LineRange(1, 2), new LineRange(1, 2), "kept"));

		var restored = Tracker(fileSystem, store);
		Assert.Null(restored.GetTurn(revertedPath));
		Assert.Equal("kept", Assert.IsType<FileChange>(restored.GetTurn(keptPath)).BaselineText);
		Assert.DoesNotContain(restored.ReviewProblems, problem => problem.Path == "reverted.txt");
	}

	[Fact]
	public void FailedDiskChangingCheckpoint_KeepsTheWrittenResultAndCannotReplayIt() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string revertedPath = "/w/reverted.txt";
		const string pendingPath = "/w/pending.txt";
		var tracker = Tracker(fileSystem, store);
		Change(fileSystem, tracker, revertedPath, "old", "new");
		Change(fileSystem, tracker, pendingPath, "base", "pending");
		store.FailNextSave = true;

		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertFile(revertedPath));

		Assert.Equal("old", fileSystem.ReadAllText(revertedPath));
		Assert.DoesNotContain(tracker.TurnChanges(), change => change.Path == revertedPath);
		Assert.Contains(tracker.ReviewProblems, problem => problem.Message.Contains("could not be saved", StringComparison.Ordinal));

		var restored = Tracker(fileSystem, store);
		Assert.Null(restored.GetTurn(revertedPath));
		Assert.NotNull(restored.GetTurn(pendingPath));
	}

	[Fact]
	public void RejectedFileGuard_InvalidatesAfterUndoCannotBeCheckpointed() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		var tracker = Tracker(fileSystem, store);
		Change(fileSystem, tracker, path, "old", "new");
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertFile(path));
		string rejectedCheckpoint = Assert.IsType<string>(store.Document);
		Assert.Contains("\"guards\":[{", rejectedCheckpoint, StringComparison.Ordinal);
		store.FailNextSave = true;

		Assert.True(tracker.UndoLastRevert().Acted);
		Assert.Equal("new", fileSystem.ReadAllText(path));
		Assert.Equal(rejectedCheckpoint, store.Document);

		var restored = Tracker(fileSystem, store);
		Assert.Null(restored.GetTurn(path));
		Assert.Contains(restored.ReviewProblems, problem => problem.Path == "file.txt");
	}

	[Fact]
	public void UnreadableHistoryGuard_InvalidatesOnlyItsHistoryAndDoesNotBlockReviewActions() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string guardedPath = "/w/guarded.txt";
		const string pendingPath = "/w/pending.txt";
		var tracker = Tracker(fileSystem, store);
		Change(fileSystem, tracker, guardedPath, "old", "new");
		Change(fileSystem, tracker, pendingPath, "base", "pending");
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertFile(guardedPath));
		Assert.True(tracker.CanUndoRevert);
		fileSystem.WriteAllBytes(guardedPath, [0]);
		bool problemsChanged = false;
		tracker.ReviewProblemsChanged += () => problemsChanged = true;

		Assert.True(tracker.KeepHunk(
			pendingPath,
			new LineRange(1, 2),
			new LineRange(1, 2),
			"pending"));

		Assert.True(problemsChanged);
		Assert.False(tracker.CanUndoRevert);
		Assert.True(tracker.CanUndoKeep);
		Assert.Contains(
			tracker.ReviewProblems,
			problem => problem.Path == "guarded.txt"
				&& problem.Message.Contains("history", StringComparison.OrdinalIgnoreCase));
		Assert.DoesNotContain("guarded.txt", Assert.IsType<string>(store.Document), StringComparison.Ordinal);

		fileSystem.WriteAllText(guardedPath, "old");
		var restored = Tracker(fileSystem, store);
		Assert.Null(restored.GetTurn(guardedPath));
		Assert.NotNull(restored.GetTurn(pendingPath));
	}

	[Fact]
	public void RetractReview_WithoutAnArmedIdentity_DoesNotCommitOrdinaryPendingChanges() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		var tracker = Tracker(fileSystem, store);
		Change(fileSystem, tracker, path, "old", "pending");
		int saveCount = store.SaveCount;
		long token = tracker.BeginReviewArm();

		Assert.False(tracker.RetractReview(token));

		var change = Assert.IsType<FileChange>(tracker.GetTurn(path));
		Assert.Equal("old", change.BaselineText);
		Assert.Equal("pending", change.CurrentText);
		Assert.Null(tracker.ActiveReviewIdentity);
		Assert.Equal(saveCount, store.SaveCount);
	}

	[Fact]
	public void FailedRevertWrite_DoesNotAdvanceProvenanceOrReviewState() {
		var backing = new InMemoryFileSystem();
		var fileSystem = new FailingWriteFileSystem(backing);
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		backing.WriteAllText(path, "old");
		var tracker = Tracker(fileSystem, store);
		tracker.CaptureBaseline(path);
		backing.WriteAllText(path, "new");
		tracker.RecordChange(path);
		fileSystem.FailWrites = true;

		Assert.Throws<IOException>(() => tracker.RevertFile(path));

		Assert.Equal("new", backing.ReadAllText(path));
		var unchanged = Assert.IsType<FileChange>(tracker.GetTurn(path));
		Assert.Equal("old", unchanged.BaselineText);
		Assert.Equal("new", unchanged.CurrentText);
		IReadOnlyList<CorrectionEdit>? correction = null;
		tracker.Corrected += edits => correction = edits;
		fileSystem.FailWrites = false;
		backing.WriteAllText(path, "user");
		tracker.RecordHandEdit(path, "user");
		Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<CorrectionEdit>>(correction));
	}

	[Fact]
	public void FailedUndoWrite_DoesNotAdvanceReviewState() {
		var backing = new InMemoryFileSystem();
		var fileSystem = new FailingWriteFileSystem(backing);
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		backing.WriteAllText(path, "old");
		var tracker = Tracker(fileSystem, store);
		tracker.CaptureBaseline(path);
		backing.WriteAllText(path, "new");
		tracker.RecordChange(path);
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertFile(path));
		fileSystem.FailWrites = true;

		Assert.Throws<IOException>(() => tracker.UndoLastRevert());

		Assert.Equal("old", backing.ReadAllText(path));
		Assert.Equal("old", Assert.IsType<FileChange>(tracker.GetTurn(path)).CurrentText);
		Assert.True(tracker.CanUndo);
		Assert.False(tracker.CanRedo);
	}

	[Fact]
	public void RevertAll_PartialWriteFailureCheckpointsTheAppliedPath() {
		var backing = new InMemoryFileSystem();
		var fileSystem = new FailingWriteFileSystem(backing);
		var store = new MemoryReviewStore();
		const string first = "/w/first.txt";
		const string second = "/w/second.txt";
		var tracker = Tracker(fileSystem, store);
		Change(backing, tracker, first, "old-first", "new-first");
		Change(backing, tracker, second, "old-second", "new-second");
		fileSystem.FailOnWrite(2);

		Assert.Throws<IOException>(() => tracker.RevertAll());

		string applied = backing.ReadAllText(first) == "old-first" ? first : second;
		string untouched = applied == first ? second : first;
		var restored = Tracker(fileSystem, store);
		Assert.Null(restored.GetTurn(applied));
		Assert.NotNull(restored.GetTurn(untouched));
		Assert.DoesNotContain(restored.ReviewProblems, problem => problem.Path == Path.GetFileName(applied));
	}

	[Fact]
	public void MultiPathUndo_PartialWriteFailureCheckpointsTheAppliedPath() {
		var backing = new InMemoryFileSystem();
		var fileSystem = new FailingWriteFileSystem(backing);
		var store = new MemoryReviewStore();
		const string first = "/w/first.txt";
		const string second = "/w/second.txt";
		var tracker = Tracker(fileSystem, store);
		Change(backing, tracker, first, "old-first", "new-first");
		Change(backing, tracker, second, "old-second", "new-second");
		Assert.True(tracker.RevertAll().Acted);
		fileSystem.FailOnWrite(2);

		Assert.Throws<IOException>(() => tracker.UndoLastRevert());

		string applied = backing.ReadAllText(first) == "new-first" ? first : second;
		string untouched = applied == first ? second : first;
		var restored = Tracker(fileSystem, store);
		Assert.NotNull(restored.GetTurn(applied));
		Assert.Null(restored.GetTurn(untouched));
		Assert.DoesNotContain(restored.ReviewProblems, problem => problem.Path == Path.GetFileName(applied));
	}

	[Fact]
	public void MultiPathRedo_PartialWriteFailureCheckpointsTheAppliedPath() {
		var backing = new InMemoryFileSystem();
		var fileSystem = new FailingWriteFileSystem(backing);
		var store = new MemoryReviewStore();
		const string first = "/w/first.txt";
		const string second = "/w/second.txt";
		var tracker = Tracker(fileSystem, store);
		Change(backing, tracker, first, "old-first", "new-first");
		Change(backing, tracker, second, "old-second", "new-second");
		Assert.True(tracker.RevertAll().Acted);
		Assert.True(tracker.UndoLastRevert().Acted);
		fileSystem.FailOnWrite(2);

		Assert.Throws<IOException>(() => tracker.Redo());

		string applied = backing.ReadAllText(first) == "old-first" ? first : second;
		string untouched = applied == first ? second : first;
		var restored = Tracker(fileSystem, store);
		Assert.Null(restored.GetTurn(applied));
		Assert.NotNull(restored.GetTurn(untouched));
		Assert.DoesNotContain(restored.ReviewProblems, problem => problem.Path == Path.GetFileName(applied));
	}

	[Fact]
	public void PartialWriteAndCheckpointFailure_RethrowsTheWriteAndRetriesTheCheckpoint() {
		var backing = new InMemoryFileSystem();
		var fileSystem = new FailingWriteFileSystem(backing);
		var store = new MemoryReviewStore();
		const string first = "/w/first.txt";
		const string second = "/w/second.txt";
		var tracker = Tracker(fileSystem, store);
		Change(backing, tracker, first, "old-first", "new-first");
		Change(backing, tracker, second, "old-second", "new-second");
		fileSystem.FailOnWrite(2);
		store.FailNextSave = true;
		bool problemsChanged = false;
		tracker.ReviewProblemsChanged += () => problemsChanged = true;

		var failure = Assert.Throws<IOException>(() => tracker.RevertAll());

		Assert.Equal("write failed", failure.Message);
		Assert.True(problemsChanged);
		Assert.Contains(
			tracker.ReviewProblems,
			problem => problem.Message.Contains("could not be saved", StringComparison.Ordinal));
		tracker.KeepFile("/w/not-tracked.txt");

		string applied = backing.ReadAllText(first) == "old-first" ? first : second;
		string untouched = applied == first ? second : first;
		var restored = Tracker(fileSystem, store);
		Assert.Null(restored.GetTurn(applied));
		Assert.NotNull(restored.GetTurn(untouched));
	}

	[Fact]
	public void KeepUndoAndRedo_OutcomesRoundTripWithoutRestoringHistory() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		var tracker = Tracker(fileSystem, store);
		Change(fileSystem, tracker, path, "old", "new");
		Assert.True(tracker.KeepHunk(path, new LineRange(1, 2), new LineRange(1, 2), "new"));

		Assert.True(tracker.UndoLastKeep().Acted);
		var afterUndo = Tracker(fileSystem, store);
		var pending = Assert.IsType<FileChange>(afterUndo.GetTurn(path));
		Assert.Equal("old", pending.AcceptedBaselineText);
		Assert.Equal("old", pending.BaselineText);
		Assert.Equal("new", pending.CurrentText);
		Assert.False(afterUndo.CanUndo);
		Assert.False(afterUndo.CanRedo);

		Assert.True(tracker.Redo().Acted);
		var afterRedo = Tracker(fileSystem, store);
		var kept = Assert.IsType<FileChange>(afterRedo.GetTurn(path));
		Assert.Equal("old", kept.AcceptedBaselineText);
		Assert.Equal("new", kept.BaselineText);
		Assert.Equal("new", kept.CurrentText);
		Assert.False(afterRedo.CanUndo);
		Assert.False(afterRedo.CanRedo);
	}

	[Fact]
	public void RevertUndoAndRedo_OutcomesRoundTripWithoutRestoringHistory() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		var tracker = Tracker(fileSystem, store);
		Change(fileSystem, tracker, path, "old", "new");
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertFile(path));

		Assert.True(tracker.UndoLastRevert().Acted);
		Assert.Equal("new", fileSystem.ReadAllText(path));
		var afterUndo = Tracker(fileSystem, store);
		Assert.NotNull(afterUndo.GetTurn(path));
		Assert.False(afterUndo.CanUndo);
		Assert.False(afterUndo.CanRedo);

		Assert.True(tracker.Redo().Acted);
		Assert.Equal("old", fileSystem.ReadAllText(path));
		var afterRedo = Tracker(fileSystem, store);
		Assert.Null(afterRedo.GetTurn(path));
		Assert.False(afterRedo.CanUndo);
		Assert.False(afterRedo.CanRedo);
	}

	[Fact]
	public void ArmedReview_MultipleTurnsRoundTripCommittedAndPendingHunks() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		const string baseline = "a\nb\nc\nd\ne\n";
		const string firstTurn = "a\nB\nc\nD\ne\n";
		const string secondTurn = "a\nB\nc\nD\nE\n";
		fileSystem.WriteAllText(path, firstTurn);
		var tracker = Tracker(fileSystem, store);
		long token = tracker.BeginReviewArm();
		var identity = new ReviewIdentity(0, "vs main", string.Empty, "base", "head", null, Root);
		Assert.True(tracker.ArmReview(
			token,
			identity,
			[new ReviewSeed(path, baseline, firstTurn, true, true)]));
		Assert.True(tracker.KeepHunk(path, new LineRange(4, 5), new LineRange(4, 5), "D"));
		tracker.Observe(new AgentPromptSubmitted("session", "continue"));
		tracker.CaptureBaseline(path);
		fileSystem.WriteAllText(path, secondTurn);
		tracker.RecordChange(path);
		Assert.True(tracker.KeepHunk(path, new LineRange(5, 6), new LineRange(5, 6), "E"));

		var restored = Tracker(fileSystem, store);

		Assert.Equal(identity, restored.ActiveReviewIdentity);
		var change = Assert.IsType<FileChange>(restored.GetTurn(path));
		Assert.Equal("a\nb\nc\nD\ne\n", change.AcceptedBaselineText);
		Assert.Equal("a\nb\nc\nD\nE\n", change.BaselineText);
		Assert.Equal(secondTurn, change.CurrentText);
		Assert.Equal(baseline, Assert.IsType<FileChange>(restored.Get(path)).BaselineText);
	}

	[Fact]
	public void ArmReview_CommitsEverySeedInOneCheckpoint() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		fileSystem.WriteAllText("/w/a.txt", "A");
		fileSystem.WriteAllText("/w/b.txt", "B");
		var tracker = Tracker(fileSystem, store);
		long token = tracker.BeginReviewArm();
		var identity = new ReviewIdentity(0, "vs main", string.Empty, "base", "head", null, Root);

		Assert.True(tracker.ArmReview(token, identity, [
			new ReviewSeed("/w/a.txt", "a", "A", true, true),
			new ReviewSeed("/w/b.txt", "b", "B", true, true),
		]));

		Assert.Equal(1, store.SaveCount);
		Assert.Equal(identity, tracker.ActiveReviewIdentity);
		Assert.Equal(2, tracker.TurnChanges().Count);
		Assert.Equal(identity, Tracker(fileSystem, store).ActiveReviewIdentity);
	}

	[Fact]
	public void MalformedCheckpoint_IsRetainedAsAReplayableProblem() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore { Document = "not json" };

		var tracker = Tracker(fileSystem, store);

		Assert.Empty(tracker.TurnChanges());
		Assert.Contains(tracker.ReviewProblems, problem => problem.Message.Contains("could not be restored", StringComparison.Ordinal));
		Assert.Equal("not json", store.Document);
	}

	[Fact]
	public void SparseCheckpoint_DoesNotCopyUnchangedFileContent() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		string unchanged = new('x', 4_096);
		string before = $"header\n{unchanged}\nold\nfooter";
		string after = $"header\n{unchanged}\nnew\nfooter";
		Change(fileSystem, Tracker(fileSystem, store), path, before, after);

		string document = Assert.IsType<string>(store.Document);

		Assert.DoesNotContain(unchanged, document, StringComparison.Ordinal);
		Assert.Contains("old", document, StringComparison.Ordinal);
	}

	[Fact]
	public void NoOpHook_DoesNotReprojectTheVisibleReviewFile() {
		var backing = new InMemoryFileSystem();
		var fileSystem = new CountingFileSystem(backing);
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		backing.WriteAllText(path, "old");
		var tracker = Tracker(fileSystem, store);
		tracker.CaptureBaseline(path);
		backing.WriteAllText(path, "new");
		tracker.RecordChange(path);
		fileSystem.ResetCounts();

		tracker.CaptureBaseline(path);

		Assert.Equal(2, fileSystem.FileExistsCalls(path));
		Assert.Equal(1, fileSystem.TextReadCalls(path));
	}

	[Fact]
	public void OneFileDecision_ReusesTheOtherVisibleFilesProjection() {
		var backing = new InMemoryFileSystem();
		var fileSystem = new CountingFileSystem(backing);
		var store = new MemoryReviewStore();
		const string changedPath = "/w/changed.txt";
		const string untouchedPath = "/w/untouched.txt";
		backing.WriteAllText(changedPath, "old");
		backing.WriteAllText(untouchedPath, "base");
		var tracker = Tracker(fileSystem, store);
		tracker.CaptureBaseline(changedPath);
		backing.WriteAllText(changedPath, "new");
		tracker.RecordChange(changedPath);
		tracker.CaptureBaseline(untouchedPath);
		backing.WriteAllText(untouchedPath, "pending");
		tracker.RecordChange(untouchedPath);
		fileSystem.ResetCounts();

		Assert.True(tracker.KeepHunk(changedPath, new LineRange(1, 2), new LineRange(1, 2), "new"));

		Assert.Equal(0, fileSystem.FileExistsCalls(untouchedPath));
		Assert.Equal(0, fileSystem.TextReadCalls(untouchedPath));
		Assert.NotNull(Tracker(fileSystem, store).GetTurn(untouchedPath));
	}

	[Fact]
	public void UnchangedHistoryGuard_DoesNotReadItsContentAgain() {
		var backing = new InMemoryFileSystem();
		var fileSystem = new CountingFileSystem(backing);
		var store = new MemoryReviewStore();
		const string guardedPath = "/w/guarded.txt";
		const string pendingPath = "/w/pending.txt";
		backing.WriteAllText(guardedPath, "old");
		backing.WriteAllText(pendingPath, "base");
		var tracker = Tracker(fileSystem, store);
		tracker.CaptureBaseline(guardedPath);
		backing.WriteAllText(guardedPath, "new");
		tracker.RecordChange(guardedPath);
		tracker.CaptureBaseline(pendingPath);
		backing.WriteAllText(pendingPath, "pending");
		tracker.RecordChange(pendingPath);
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertFile(guardedPath));
		fileSystem.ResetCounts();

		Assert.True(tracker.KeepHunk(pendingPath, new LineRange(1, 2), new LineRange(1, 2), "pending"));

		Assert.Equal(0, fileSystem.TextReadCalls(guardedPath));
	}

	[Fact]
	public void ArmReview_DeclinesWhenASeedMovedBeforeTheAtomicCommit() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		fileSystem.WriteAllText(path, "captured");
		var tracker = Tracker(fileSystem, store);
		long token = tracker.BeginReviewArm();
		fileSystem.WriteAllText(path, "moved");

		bool armed = tracker.ArmReview(
			token,
			new ReviewIdentity(0, "vs main", string.Empty, "base", "head", null, Root),
			[new ReviewSeed(path, "base", "captured", true, true)]);

		Assert.False(armed);
		Assert.Null(tracker.ActiveReviewIdentity);
		Assert.Empty(tracker.TurnChanges());
	}

	[Fact]
	public void ArmReview_StaleTokenCannotReplaceTheNewerReviewOrCheckpoint() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		fileSystem.WriteAllText(path, "current");
		var tracker = Tracker(fileSystem, store);
		long staleToken = tracker.BeginReviewArm();
		long currentToken = tracker.BeginReviewArm();
		var currentIdentity = new ReviewIdentity(0, "vs current", string.Empty, "base", "head", null, Root);
		Assert.True(tracker.ArmReview(
			currentToken,
			currentIdentity,
			[new ReviewSeed(path, "current base", "current", true, true)]));
		string checkpoint = Assert.IsType<string>(store.Document);
		int saveCount = store.SaveCount;

		bool armed = tracker.ArmReview(
			staleToken,
			new ReviewIdentity(0, "vs stale", string.Empty, "stale-base", "stale-head", null, Root),
			[new ReviewSeed(path, "stale base", "current", true, true)]);

		Assert.False(armed);
		Assert.Equal(currentIdentity, tracker.ActiveReviewIdentity);
		Assert.Equal("current base", Assert.IsType<FileChange>(tracker.GetTurn(path)).BaselineText);
		Assert.Equal(checkpoint, store.Document);
		Assert.Equal(saveCount, store.SaveCount);
	}

	[Fact]
	public void ArmReview_CheckpointFailureRestoresThePriorBoardAndIdentity() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		fileSystem.WriteAllText(path, "current");
		var tracker = Tracker(fileSystem, store);
		long firstToken = tracker.BeginReviewArm();
		var firstIdentity = new ReviewIdentity(0, "vs first", string.Empty, "first-base", "first-head", null, Root);
		Assert.True(tracker.ArmReview(
			firstToken,
			firstIdentity,
			[new ReviewSeed(path, "first", "current", true, true)]));
		string checkpoint = Assert.IsType<string>(store.Document);
		long secondToken = tracker.BeginReviewArm();
		store.FailNextSave = true;

		Assert.Throws<ReviewPersistenceException>(() => tracker.ArmReview(
			secondToken,
			new ReviewIdentity(0, "vs second", string.Empty, "second-base", "second-head", null, Root),
			[new ReviewSeed(path, "second", "current", true, true)]));

		Assert.Equal(firstIdentity, tracker.ActiveReviewIdentity);
		Assert.Equal("first", Assert.IsType<FileChange>(tracker.GetTurn(path)).BaselineText);
		Assert.Equal(checkpoint, store.Document);
		Assert.Equal(firstIdentity, Tracker(fileSystem, store).ActiveReviewIdentity);
	}

	[Fact]
	public void FullyInvalidatedReview_DoesNotLeaveAStaleIdentity() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		fileSystem.WriteAllText(path, "current");
		var tracker = Tracker(fileSystem, store);
		long token = tracker.BeginReviewArm();
		Assert.True(tracker.ArmReview(
			token,
			new ReviewIdentity(0, "vs main", string.Empty, "base", "head", null, Root),
			[new ReviewSeed(path, "base", "current", true, true)]));
		fileSystem.WriteAllText(path, "external");

		var restored = Tracker(fileSystem, store);

		Assert.Null(restored.ActiveReviewIdentity);
		Assert.Equal(0, restored.ActiveReviewToken);
		Assert.Contains(restored.ReviewProblems, problem => problem.Path == "file.txt");
	}

	[Fact]
	public void OneOrigin_MayRestoreWithPendingAndAcceptedForms() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		Change(fileSystem, Tracker(fileSystem, store), path, "a\nb", "A\nB");
		var tracker = Tracker(fileSystem, store);
		Assert.True(tracker.KeepHunk(path, new LineRange(1, 2), new LineRange(1, 2), "A"));

		var restored = Tracker(fileSystem, store);

		Assert.NotNull(restored.GetTurn(path));
		Assert.DoesNotContain(restored.ReviewProblems, problem => problem.Message.Contains("provenance", StringComparison.Ordinal));
	}

	[Fact]
	public void OneOrigin_WithInconsistentPromptsIsRejected() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore();
		const string path = "/w/file.txt";
		Change(fileSystem, Tracker(fileSystem, store), path, "a\nb", "A\nB");
		var tracker = Tracker(fileSystem, store);
		Assert.True(tracker.KeepHunk(path, new LineRange(1, 2), new LineRange(1, 2), "A"));
		var root = JsonNode.Parse(Assert.IsType<string>(store.Document));
		var origins = root!["files"]![0]!["provenance"]!["origins"]!.AsArray();
		Assert.Equal(2, origins.Count);
		origins[0]!["prompt"] = "first";
		origins[1]!["prompt"] = "second";
		store.Document = root.ToJsonString();

		var restored = Tracker(fileSystem, store);

		Assert.Empty(restored.TurnChanges());
		Assert.Contains(restored.ReviewProblems, problem => problem.Message.Contains("inconsistent prompts", StringComparison.Ordinal));
	}

	[Fact]
	public void NullCheckpointCollections_AreReportedInsteadOfCrashingConstruction() {
		var fileSystem = new InMemoryFileSystem();
		var store = new MemoryReviewStore {
			Document = """{"version":1,"review":null,"armToken":0,"activeReviewToken":0,"nextOriginId":0,"files":null,"guards":[]}""",
		};

		var tracker = Tracker(fileSystem, store);

		Assert.Contains(tracker.ReviewProblems, problem => problem.Message.Contains("invalid shape", StringComparison.Ordinal));
	}

	private static SessionChangeTracker Tracker(IFileSystem fileSystem, IReviewCheckpointStore store) =>
		new(fileSystem, Root, path => path.StartsWith(Root, StringComparison.Ordinal), store);

	private static void Change(InMemoryFileSystem fileSystem, SessionChangeTracker tracker, string path, string before, string after) {
		fileSystem.WriteAllText(path, before);
		tracker.CaptureBaseline(path);
		fileSystem.WriteAllText(path, after);
		tracker.RecordChange(path);
	}

	private sealed class MemoryReviewStore : IReviewCheckpointStore {
		public string? Document { get; set; }
		public bool FailNextSave { get; set; }
		public int SaveCount { get; private set; }

		public string? Load() => Document;

		public void Save(string document) {
			if (FailNextSave) {
				FailNextSave = false;
				throw new IOException("checkpoint failed");
			}
			Document = document;
			SaveCount++;
		}

		public void Clear() => Document = null;
	}

	private sealed class FailingWriteFileSystem(IFileSystem inner) : IFileSystem {
		private int? _failOnWrite;
		private int _writeCount;

		public bool FailWrites { get; set; }

		public void FailOnWrite(int number) {
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(number);
			_failOnWrite = number;
			_writeCount = 0;
		}

		public bool FileExists(string path) => inner.FileExists(path);
		public bool DirectoryExists(string path) => inner.DirectoryExists(path);
		public bool TryGetStat(string path, out FileStat stat) => inner.TryGetStat(path, out stat);
		public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) => inner.EnumerateDirectory(path);
		public string ReadAllText(string path) => inner.ReadAllText(path);
		public bool TryReadAllText(string path, out string contents) => inner.TryReadAllText(path, out contents);
		public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);

		public void WriteAllText(string path, string contents) {
			if (FailWrites || _failOnWrite is { } fail && ++_writeCount == fail) {
				throw new IOException("write failed");
			}
			inner.WriteAllText(path, contents);
		}

		public void WriteAllBytes(string path, byte[] contents) => inner.WriteAllBytes(path, contents);
		public void AppendAllText(string path, string contents) => inner.AppendAllText(path, contents);
		public void WriteAllTextAtomic(string path, string contents) => inner.WriteAllTextAtomic(path, contents);
		public void DeleteFile(string path) => inner.DeleteFile(path);
	}

	private sealed class CountingFileSystem(IFileSystem inner) : IFileSystem {
		private readonly Dictionary<string, int> _fileExistsCalls = new(StringComparer.Ordinal);
		private readonly Dictionary<string, int> _textReadCalls = new(StringComparer.Ordinal);

		public int FileExistsCalls(string path) => _fileExistsCalls.GetValueOrDefault(path);
		public int TextReadCalls(string path) => _textReadCalls.GetValueOrDefault(path);

		public void ResetCounts() {
			_fileExistsCalls.Clear();
			_textReadCalls.Clear();
		}

		public bool FileExists(string path) {
			_fileExistsCalls[path] = FileExistsCalls(path) + 1;
			return inner.FileExists(path);
		}

		public bool DirectoryExists(string path) => inner.DirectoryExists(path);
		public bool TryGetStat(string path, out FileStat stat) => inner.TryGetStat(path, out stat);
		public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) => inner.EnumerateDirectory(path);

		public string ReadAllText(string path) {
			_textReadCalls[path] = TextReadCalls(path) + 1;
			return inner.ReadAllText(path);
		}

		public bool TryReadAllText(string path, out string contents) {
			_textReadCalls[path] = TextReadCalls(path) + 1;
			return inner.TryReadAllText(path, out contents);
		}

		public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);
		public void WriteAllText(string path, string contents) => inner.WriteAllText(path, contents);
		public void WriteAllBytes(string path, byte[] contents) => inner.WriteAllBytes(path, contents);
		public void AppendAllText(string path, string contents) => inner.AppendAllText(path, contents);
		public void WriteAllTextAtomic(string path, string contents) => inner.WriteAllTextAtomic(path, contents);
		public void DeleteFile(string path) => inner.DeleteFile(path);
	}
}
