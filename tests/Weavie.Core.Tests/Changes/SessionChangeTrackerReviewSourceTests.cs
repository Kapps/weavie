using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Attaching a forge review extends the current board instead of replacing its decisions.</summary>
public sealed class SessionChangeTrackerReviewSourceTests {
	private static readonly string Root = Path.GetFullPath("/w");
	private static readonly string File = Path.Combine(Root, "review.txt");
	private static readonly ReviewContext Review = new(101, "#101", "topic", "base", "head",
		new("github.com", "test", "repo"), Root, "base", "topic");
	private static SessionChangeTracker Tracker(InMemoryFileSystem files, IReviewPersistence persistence) =>
		new(files, NoopFileActivitySink.Instance, Root, path => Path.GetDirectoryName(path) == Root, SessionChangeTracker.NothingIgnored, persistence);

	[Fact]
	public void FirstPrOpenAndReopen_PreserveKeptRejectedAndPendingChanges() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		const string baseline = "a\nspace1\nb\nspace2\nc\n";
		files.WriteAllText(File, baseline);
		var tracker = Tracker(files, persistence);
		tracker.CaptureBaseline(File);
		files.WriteAllText(File, "A\nspace1\nB\nspace2\nC\n");
		tracker.RecordChange(File);
		Assert.True(tracker.KeepHunk(File, new(1, 2), new(1, 2), "A"));
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertHunk(File, new(3, 4), new(3, 4), "B"));
		var before = tracker.GetTurn(File)!;
		ReviewSeed[] seeds = [new(File, baseline, files.ReadAllText(File), true, true)];

		tracker.ArmReview(Review, seeds);
		Assert.Equivalent(before, tracker.GetTurn(File), strict: true);
		tracker = Tracker(files, persistence);
		tracker.ArmReview(Review with { HeadSha = "updated-head" }, seeds);
		Assert.Equivalent(before, tracker.GetTurn(File), strict: true);
		Assert.Equal("updated-head", tracker.Review!.HeadSha);
		Assert.True(tracker.UndoLastRevert().Acted);
		Assert.Equal("A\nspace1\nB\nspace2\nC\n", files.ReadAllText(File));
		Assert.True(tracker.UndoLastKeep().Acted);
		Assert.Equal(baseline, tracker.GetTurn(File)!.BaselineText);
	}

	[Fact]
	public void Reopen_DoesNotReseedARejectedCreation_AndDiscoversNewFiles() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "created in PR\n");
		var tracker = Tracker(files, persistence);
		tracker.ArmReview(Review, [new(File, "", "created in PR\n", false, true)]);
		Assert.Equal(RevertHunkOutcome.Deleted, tracker.RevertFile(File));
		string added = Path.Combine(Root, "later.txt");
		files.WriteAllText(added, "new head proposal\n");

		tracker = Tracker(files, persistence);
		tracker.ArmReview(Review with { HeadSha = "new-head" }, [
			new(File, "", "", false, false), new(added, "", "new head proposal\n", false, true),
		]);
		Assert.False(files.FileExists(File));
		Assert.True(tracker.CanUndoRevert);
		Assert.Equal("new head proposal\n", tracker.GetTurn(added)!.CurrentText);
		Assert.True(tracker.UndoLastRevert().Acted);
		Assert.Equal("created in PR\n", files.ReadAllText(File));
	}

	[Fact]
	public void OverlappingBaseExtension_RestartsTheFileAgainstTheNewBase() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "a\nspace\nb\n");
		var tracker = Tracker(files, persistence);
		tracker.CaptureBaseline(File);
		files.WriteAllText(File, "A\nspace\nB\n");
		tracker.RecordChange(File);
		Assert.True(tracker.KeepHunk(File, new(1, 2), new(1, 2), "A"));
		string other = Path.Combine(Root, "other.txt");
		files.WriteAllText(other, "new file\n");
		IReadOnlyList<string>? restarted = null;
		tracker.ReviewFilesRestarted += paths => restarted = paths;

		tracker.ArmReview(Review, [
			new(other, "", "new file\n", false, true), new(File, "x\nspace\nb\n", "A\nspace\nB\n", true, true),
		]);

		Assert.Equal([File], restarted);
		Assert.Equal(Review, tracker.Review);
		var change = tracker.GetTurn(File)!;
		Assert.Equal("x\nspace\nb\n", change.BaselineText);
		Assert.Equal("x\nspace\nb\n", change.AcceptedBaselineText);
		Assert.NotNull(tracker.GetTurn(other));
		Assert.Equal("A\nspace\nB\n", files.ReadAllText(File));
	}

	[Fact]
	public void EarlierBaseExtension_AddsCommittedChangesAsPending_WithoutRependingKeptChanges() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "committed\nspace\nold\n");
		var tracker = Tracker(files, persistence);
		ReviewTestChanges.AddPendingFile(tracker, files, Path.Combine(Root, "pending.txt"));
		tracker.CaptureBaseline(File);
		files.WriteAllText(File, "committed\nspace\nkept\n");
		tracker.RecordChange(File);
		Assert.True(tracker.KeepHunk(File, new(3, 4), new(3, 4), "kept"));

		tracker.ArmReview(Review, [new(File, "original\nspace\nold\n", files.ReadAllText(File), true, true)]);
		var change = Tracker(files, persistence).GetTurn(File)!;
		Assert.Equal("original\nspace\nold\n", change.AcceptedBaselineText);
		Assert.Equal("original\nspace\nkept\n", change.BaselineText);
		Assert.Equal("committed\nspace\nkept\n", change.CurrentText);
		Assert.True(tracker.UndoLastKeep().Acted);
		Assert.Equal("original\nspace\nold\n", tracker.GetTurn(File)!.BaselineText);
	}

	[Fact]
	public void ReopenAfterHeadUpdate_AddsNewEditsToAnExistingReviewedFile() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "kept\nspace\nold\n");
		var tracker = Tracker(files, persistence);
		ReviewTestChanges.AddPendingFile(tracker, files, Path.Combine(Root, "pending.txt"));
		tracker.ArmReview(Review, [new(File, "original\nspace\nold\n", files.ReadAllText(File), true, true)]);
		tracker.KeepFile(File);
		files.WriteAllText(File, "kept\nspace\nnew head edit\n");

		tracker.ArmReview(Review with { HeadSha = "new-head" }, [new(File,
			"original\nspace\nold\n", files.ReadAllText(File), true, true)]);
		var change = tracker.GetTurn(File)!;
		Assert.Equal("kept\nspace\nold\n", change.BaselineText);
		Assert.Equal("kept\nspace\nnew head edit\n", change.CurrentText);
		Assert.True(tracker.UndoLastKeep().Acted);
		Assert.Equal("kept\nspace\nnew head edit\n", files.ReadAllText(File));
	}

	[Fact]
	public void AReviewOnAnotherBranchIsAnotherSource() {
		Assert.True(Review.SameSource(Review with { MergeBase = "moved", HeadSha = "newer" }));
		Assert.False(Review.SameSource(Review with { Branch = "other" }));
	}

	[Fact]
	public void AMovedMergeBase_RebasesTheReview_KeepingDecisionsItDoesNotTouch() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		const string original = "a\nspace1\nb\nspace2\nc\n";
		files.WriteAllText(File, "A\nspace1\nb\nspace2\nc\n");
		var tracker = Tracker(files, persistence);
		tracker.ArmReview(Review, [new(File, original, files.ReadAllText(File), true, true)]);
		Assert.True(tracker.KeepHunk(File, new(1, 2), new(1, 2), "A"));

		// The target's merged change to the last line is now part of the base, not of this branch's review.
		files.WriteAllText(File, "A\nspace1\nb\nspace2\nC\n");
		tracker.RecordChange(File);
		var before = tracker.GetTurn(File)!;
		tracker.ArmReview(Review with { MergeBase = "merged" }, [new(File, "a\nspace1\nb\nspace2\nC\n", files.ReadAllText(File), true, true)]);

		var change = tracker.GetTurn(File)!;
		Assert.Equal("merged", tracker.Review!.MergeBase);
		Assert.Equal(before.AcceptedBaselineText.Replace("c\n", "C\n"), change.AcceptedBaselineText);
		Assert.Equal(before.BaselineText.Replace("c\n", "C\n"), change.BaselineText);
		Assert.StartsWith("A\n", change.BaselineText);
		Assert.Equal(change.CurrentText, change.BaselineText); // nothing left pending: the kept line and the merged base
	}

	[Fact]
	public void RebaseReview_LeavesAClosedOrReplacedReviewAlone() {
		var files = new InMemoryFileSystem();
		var tracker = Tracker(files, new MemoryReviewPersistence());
		files.WriteAllText(File, "changed\n");
		ReviewSeed[] seeds = [new(File, "base\n", "changed\n", true, true)];
		tracker.ArmReview(Review, seeds);
		tracker.CloseReview();

		Assert.False(tracker.RebaseReview(Review, Review with { MergeBase = "merged" }, seeds));
		Assert.Null(tracker.Review);
	}

	[Fact]
	public void RetireReview_ClosesOnlyTheReviewItExpects() {
		var files = new InMemoryFileSystem();
		var tracker = Tracker(files, new MemoryReviewPersistence());
		files.WriteAllText(File, "changed\n");
		tracker.ArmReview(Review, [new(File, "base\n", "changed\n", true, true)]);

		Assert.False(tracker.RetireReview(Review with { MergeBase = "stale" }));
		Assert.NotNull(tracker.Review);
		Assert.True(tracker.RetireReview(Review));
		Assert.Null(tracker.Review);
	}
}
