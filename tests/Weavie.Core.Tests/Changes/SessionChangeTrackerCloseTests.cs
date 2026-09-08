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
	[InlineData("kept")]
	[InlineData("rejected")]
	[InlineData("undone")]
	public void Close_PreservesDiskAndClearsReviewAfterRestart(string decision) {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "proposal\n");
		var tracker = Tracker(files, persistence);
		tracker.ArmReview(Review, [new(File, "old\n", "proposal\n", true, true)]);
		if (decision == "kept") tracker.AcceptTurn();
		if (decision is "rejected" or "undone") tracker.RevertFile(File);
		if (decision == "undone") Assert.True(tracker.UndoLastRevert().Acted);
		string disk = files.ReadAllText(File);

		tracker.CloseReview();
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
