using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Finishing a review is durable and leaves the working tree intact.</summary>
public sealed class SessionChangeTrackerCloseTests {
	private static readonly string Root = Path.GetFullPath("/w");
	private static readonly string File = Path.Combine(Root, "review.txt");
	private static readonly ReviewContext Review = new(0, "vs main", "", "base", "head", null, Root);
	private static SessionChangeTracker Tracker(InMemoryFileSystem files, IReviewPersistence persistence) =>
		new(files, NoopFileActivitySink.Instance, Root, path => Path.GetDirectoryName(path) == Root, persistence);

	[Theory]
	[InlineData("pending")]
	[InlineData("hunk")]
	[InlineData("file")]
	[InlineData("rejected")]
	[InlineData("undone")]
	public void FinalAcceptanceOrClose_PreservesDiskAndClearsReviewAfterRestart(string decision) {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "proposal\n");
		var tracker = Tracker(files, persistence);
		tracker.ArmReview(Review, [new(File, "old\n", "proposal\n", true, true)]);
		if (decision == "hunk") Assert.True(tracker.KeepHunk(File, new(1, 2), new(1, 2), "proposal"));
		if (decision == "file") tracker.KeepFile(File);
		if (decision is "rejected" or "undone") tracker.RevertFile(File);
		if (decision == "undone") Assert.True(tracker.UndoLastRevert().Acted);
		string disk = files.ReadAllText(File);

		if (decision is not ("hunk" or "file")) tracker.CloseReview();
		Assert.Empty(tracker.TurnChanges());
		Assert.Null(tracker.Review);
		Assert.Equal(disk, files.ReadAllText(File));
		Assert.False(tracker.CanUndoKeep);
		Assert.False(tracker.CanUndoRevert);
		Assert.False(tracker.CanRedo);
		tracker = Tracker(files, persistence);
		Assert.Empty(tracker.TurnChanges());
		Assert.Null(tracker.Review);
		Assert.False(tracker.UndoLastKeep().Acted);
		Assert.False(tracker.UndoLastRevert().Acted);
		Assert.False(tracker.Redo().Acted);
		Assert.Equal(disk, files.ReadAllText(File));

		tracker.CaptureBaseline(File);
		files.WriteAllText(File, "next proposal\n");
		tracker.RecordChange(File);
		var next = Assert.Single(tracker.TurnChanges());
		Assert.Equal(disk, next.AcceptedBaselineText);
		Assert.Equal(disk, next.BaselineText);
		Assert.Equal("next proposal\n", next.CurrentText);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void LastFileAcceptance_WaitsForExistenceOnlyChangesAndPersistsClosure(bool currentExists) {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		string empty = Path.Combine(Root, "empty.txt");
		files.WriteAllText(File, "proposal\n");
		if (currentExists) files.WriteAllText(empty, "");
		var tracker = Tracker(files, persistence);
		tracker.ArmReview(Review, [new(File, "old\n", "proposal\n", true, true),
			new(empty, "", "", !currentExists, currentExists)]);
		tracker.KeepFile(File);
		Assert.True(tracker.CanUndoKeep);
		Assert.NotNull(tracker.Review);
		Assert.Equal(2, tracker.TurnChanges().Count);

		tracker.KeepFile(empty);
		Assert.Empty(tracker.TurnChanges());
		Assert.False(tracker.CanUndoKeep);
		Assert.Equal(currentExists, files.FileExists(empty));
		tracker = Tracker(files, persistence);
		Assert.Empty(tracker.TurnChanges());
		Assert.Null(tracker.Review);
		Assert.Equal(currentExists, tracker.GetTurn(empty)!.BaselineExists);
	}

	[Fact]
	public void RedoLastAcceptance_ClosesWhenOtherPendingChangesWereRemoved() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		string other = Path.Combine(Root, "other.txt");
		files.WriteAllText(File, "proposal\n");
		files.WriteAllText(other, "other proposal\n");
		var tracker = Tracker(files, persistence);
		tracker.ArmReview(Review, [new(File, "old\n", "proposal\n", true, true),
			new(other, "other old\n", "other proposal\n", true, true)]);
		tracker.KeepFile(File);
		Assert.True(tracker.UndoLastKeep().Acted);
		files.WriteAllText(other, "other old\n");

		Assert.True(tracker.Redo().Acted);
		Assert.Empty(tracker.TurnChanges());
		Assert.False(tracker.CanUndoKeep);
		tracker = Tracker(files, persistence);
		Assert.Empty(tracker.TurnChanges());
		Assert.Null(tracker.Review);
		Assert.Equal("proposal\n", files.ReadAllText(File));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Completion_InvalidatesInFlightSourceEvenAfterANewAgentEdit(bool explicitClose) {
		var files = new InMemoryFileSystem();
		var tracker = Tracker(files, new MemoryReviewPersistence());
		files.WriteAllText(File, "proposal\n");
		ReviewSeed[] seeds = [new(File, "old\n", "proposal\n", true, true)];
		tracker.ArmReview(Review, seeds);
		object oldRequest = tracker.BeginReviewRequest();
		if (explicitClose) tracker.CloseReview();
		else tracker.KeepFile(File);
		tracker.CaptureBaseline(File);
		files.WriteAllText(File, "next proposal\n");
		tracker.RecordChange(File);

		Assert.False(tracker.ArmReview(Review, seeds, oldRequest));
		Assert.Null(tracker.Review);
		var next = Assert.Single(tracker.TurnChanges());
		Assert.Equal("proposal\n", next.BaselineText);
		Assert.Equal("next proposal\n", next.CurrentText);
	}

	[Fact]
	public void SourceRequest_OnlyTheNewestRequestCanArm() {
		var files = new InMemoryFileSystem();
		var tracker = Tracker(files, new MemoryReviewPersistence());
		files.WriteAllText(File, "proposal\n");
		object oldRequest = tracker.BeginReviewRequest();
		object newRequest = tracker.BeginReviewRequest();
		ReviewSeed[] seeds = [new(File, "old\n", "proposal\n", true, true)];
		Assert.False(tracker.ArmReview(Review, seeds, oldRequest));
		Assert.True(tracker.ArmReview(Review, seeds, newRequest));
		Assert.NotNull(tracker.Review);
	}

	[Fact]
	public void LateUnkeepRequest_CannotReopenACompletedInsertion() {
		var files = new InMemoryFileSystem();
		var tracker = Tracker(files, new MemoryReviewPersistence());
		files.WriteAllText(File, "old\ninserted\n");
		tracker.ArmReview(Review, [new(File, "old\n", "old\ninserted\n", true, true)]);
		Assert.True(tracker.KeepHunk(File, new(2, 2), new(2, 3), "inserted"));
		Assert.False(tracker.UnkeepHunk(File, new(2, 2), new(2, 3), "", "inserted"));
		Assert.Empty(tracker.TurnChanges());
		Assert.Equal("old\ninserted\n", files.ReadAllText(File));
	}

	[Fact]
	public void FailedLastHunkGuard_DoesNotCloseReview() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "proposal\n");
		var tracker = Tracker(files, persistence);
		tracker.ArmReview(Review, [new(File, "old\n", "proposal\n", true, true)]);
		Assert.False(tracker.KeepHunk(File, new(1, 2), new(1, 2), "stale"));
		tracker = Tracker(files, persistence);
		Assert.Single(tracker.TurnChanges());
		Assert.NotNull(tracker.Review);
		Assert.Equal("old\n", tracker.GetTurn(File)!.BaselineText);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ClosedReview_CanOpenTheSameOrAnotherSource(bool differentSource) {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "proposal\n");
		var tracker = Tracker(files, persistence);
		tracker.ArmReview(Review, [new(File, "old\n", "proposal\n", true, true)]);
		tracker.CloseReview();
		tracker = Tracker(files, persistence);
		var source = differentSource ? Review with { Label = "vs other", MergeBase = "other-base" } : Review;
		string baseline = differentSource ? "different base\n" : "old\n";
		tracker.ArmReview(source, [new(File, baseline, "proposal\n", true, true)]);
		var reopened = Assert.Single(tracker.TurnChanges());
		Assert.Equal(baseline, reopened.AcceptedBaselineText);
		Assert.Equal(baseline, reopened.BaselineText);
		Assert.Equal("proposal\n", files.ReadAllText(File));
		Assert.Equal(source, tracker.Review);
	}

	[Fact]
	public void Close_EmptyPersistedSourceReleasesItsIdentity() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		var tracker = Tracker(files, persistence);
		tracker.ArmReview(Review, []);
		tracker = Tracker(files, persistence);
		Assert.NotNull(tracker.Review);
		tracker.CloseReview();
		tracker = Tracker(files, persistence);
		Assert.Null(tracker.Review);
		tracker.ArmReview(Review with { Label = "vs other", MergeBase = "other-base" }, []);
		Assert.Equal("vs other", tracker.Review!.Label);
	}

	[Fact]
	public void Reopen_IncludesHandEditsSavedAfterClosing() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "proposal\n");
		var tracker = Tracker(files, persistence);
		tracker.ArmReview(Review, [new(File, "old\n", "proposal\n", true, true)]);
		tracker.CloseReview();
		files.WriteAllText(File, "proposal\nuser addition\n");
		tracker.RecordHandEdit(File, files.ReadAllText(File));
		tracker.ArmReview(Review, [new(File, "old\n", files.ReadAllText(File), true, true)]);
		Assert.Equal(files.ReadAllText(File), Assert.Single(tracker.TurnChanges()).CurrentText);
	}

	[Fact]
	public void Close_RejectedCreationStaysAbsentAndLaterCreationStartsFresh() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "created\n");
		var tracker = Tracker(files, persistence);
		tracker.ArmReview(Review, [new(File, "", "created\n", false, true)]);
		Assert.Equal(RevertHunkOutcome.Deleted, tracker.RevertFile(File));
		tracker.CloseReview();
		tracker = Tracker(files, persistence);
		Assert.Empty(tracker.TurnChanges());
		Assert.False(files.FileExists(File));
		tracker.CaptureBaseline(File);
		files.WriteAllText(File, "recreated\n");
		tracker.RecordChange(File);
		var recreated = Assert.Single(tracker.TurnChanges());
		Assert.False(recreated.AcceptedBaselineExists);
		Assert.False(recreated.BaselineExists);
		Assert.True(recreated.CurrentExists);
	}
}
