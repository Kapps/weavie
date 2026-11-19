using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Closing or finishing a review scraps it durably and leaves the working tree intact.</summary>
public sealed class SessionChangeTrackerCloseTests {
	private static readonly string Root = Path.GetFullPath("/w");
	private static readonly string File = Path.Combine(Root, "review.txt");
	private static readonly ReviewContext Review = ReviewTestChanges.GitReview(Root);
	private static SessionChangeTracker Tracker(InMemoryFileSystem files, IReviewPersistence persistence) =>
		new(files, NoopFileActivitySink.Instance, Root, path => Path.GetDirectoryName(path) == Root, SessionChangeTracker.NothingIgnored, persistence);

	private static void AgentDelete(SessionChangeTracker tracker, InMemoryFileSystem files, string path) {
		tracker.CaptureBaseline(path);
		files.DeleteFile(path);
		tracker.Observe(new AgentToolCompleted(new AgentMutation.None()));
	}

	[Theory]
	[InlineData("pending")]
	[InlineData("hunk")]
	[InlineData("file")]
	[InlineData("rejected")]
	[InlineData("undone")]
	public void Close_PreservesDiskAndClearsReviewAfterRestart(string decision) {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "proposal\n");
		var tracker = Tracker(files, persistence);
		tracker.StartReview(Review, ReviewTestChanges.Seed(files, File, "old\n"));
		if (decision == "hunk") Assert.True(tracker.KeepHunk(File, new(1, 2), new(1, 2), "proposal"));
		if (decision == "file") tracker.KeepFile(File);
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

		ReviewTestChanges.AgentEdit(tracker, files, File, "next proposal\n");
		var next = Assert.Single(tracker.TurnChanges());
		Assert.Equal(disk, next.AcceptedBaselineText);
		Assert.Equal(disk, next.BaselineText);
		Assert.Equal("next proposal\n", next.CurrentText);
	}

	[Theory]
	[InlineData("\r\n", "\n")]
	[InlineData("\n", "\r\n")]
	public void LastHunkAcceptance_ScrapsTheDefaultReviewAcrossLineEndingChanges(string baselineEol, string currentEol) {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		string current = $"proposal{currentEol}new tail{currentEol}";
		files.WriteAllText(File, $"old{baselineEol}tail{baselineEol}");
		var tracker = Tracker(files, persistence);
		ReviewTestChanges.AgentEdit(tracker, files, File, current);
		Assert.True(tracker.KeepHunk(File, new(1, 2), new(1, 2), "proposal"));
		Assert.True(tracker.CanUndoKeep);

		Assert.True(tracker.KeepHunk(File, new(2, 3), new(2, 3), "new tail"));
		Assert.Empty(tracker.TurnChanges());
		Assert.Null(tracker.GetTurn(File));
		Assert.False(tracker.CanUndoKeep);
		tracker = Tracker(files, persistence);
		Assert.Empty(tracker.TurnChanges());
		Assert.Null(tracker.GetTurn(File));
		Assert.Equal(current, files.ReadAllText(File));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void LastFileAcceptance_WaitsForExistenceOnlyChangesAndPersistsTheScrap(bool currentExists) {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		string empty = Path.Combine(Root, "empty.txt");
		files.WriteAllText(File, "old\n");
		if (!currentExists) files.WriteAllText(empty, "");
		var tracker = Tracker(files, persistence);
		ReviewTestChanges.AgentEdit(tracker, files, File, "proposal\n");
		if (currentExists) ReviewTestChanges.AgentEdit(tracker, files, empty, "");
		else AgentDelete(tracker, files, empty);
		tracker.KeepFile(File);
		Assert.True(tracker.CanUndoKeep);
		Assert.Equal(2, tracker.TurnChanges().Count);

		tracker.KeepFile(empty);
		Assert.Empty(tracker.TurnChanges());
		Assert.False(tracker.CanUndoKeep);
		Assert.Equal(currentExists, files.FileExists(empty));
		tracker = Tracker(files, persistence);
		Assert.Empty(tracker.TurnChanges());
		Assert.Null(tracker.GetTurn(empty));
	}

	[Fact]
	public void RedoLastAcceptance_ScrapsWhenOtherPendingChangesWereRemoved() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		string other = Path.Combine(Root, "other.txt");
		files.WriteAllText(File, "old\n");
		files.WriteAllText(other, "other old\n");
		var tracker = Tracker(files, persistence);
		ReviewTestChanges.AgentEdit(tracker, files, File, "proposal\n");
		ReviewTestChanges.AgentEdit(tracker, files, other, "other proposal\n");
		tracker.KeepFile(File);
		Assert.True(tracker.UndoLastKeep().Acted);
		files.WriteAllText(other, "other old\n");
		tracker.RecordHandEdit(other, "other old\n");

		Assert.True(tracker.Redo().Acted);
		Assert.Empty(tracker.TurnChanges());
		Assert.False(tracker.CanUndoKeep);
		tracker = Tracker(files, persistence);
		Assert.Empty(tracker.TurnChanges());
		Assert.Equal("proposal\n", files.ReadAllText(File));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Scrapping_LeavesAReviewTheUserStartedToOpen(bool explicitClose) {
		var files = new InMemoryFileSystem();
		var tracker = Tracker(files, new MemoryReviewPersistence());
		files.WriteAllText(File, "old\n");
		ReviewTestChanges.AgentEdit(tracker, files, File, "proposal\n");
		var request = tracker.BeginReviewRequest();
		if (explicitClose) tracker.CloseReview();
		else tracker.KeepFile(File);
		ReviewTestChanges.AgentEdit(tracker, files, File, "next proposal\n");

		Assert.NotNull(tracker.ReplaceReview(Review, [ReviewTestChanges.Seed(files, File, "old\n")], request));

		Assert.Equal(Review, tracker.Review);
		var next = Assert.Single(tracker.TurnChanges());
		Assert.Equal("old\n", next.BaselineText);
		Assert.Equal("next proposal\n", next.CurrentText);
	}

	[Fact]
	public void Close_CancelsARelistOfTheReviewItClosed() {
		var files = new InMemoryFileSystem();
		var tracker = Tracker(files, new MemoryReviewPersistence());
		files.WriteAllText(File, "proposal\n");
		ReviewTestChanges.StartReview(tracker, Review, ReviewTestChanges.Seed(files, File, "old\n"));
		var relist = tracker.BeginRelist(Review)!;

		tracker.CloseReview();

		Assert.Null(tracker.ReplaceReview(Review, [ReviewTestChanges.Seed(files, File, "old\n")], relist));
		Assert.Null(tracker.Review);
	}

	[Fact]
	public void LateUnkeepRequest_CannotReopenACompletedInsertion() {
		var files = new InMemoryFileSystem();
		var tracker = Tracker(files, new MemoryReviewPersistence());
		files.WriteAllText(File, "old\n");
		ReviewTestChanges.AgentEdit(tracker, files, File, "old\ninserted\n");
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
		tracker.StartReview(Review, ReviewTestChanges.Seed(files, File, "old\n"));
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
		tracker.StartReview(Review, ReviewTestChanges.Seed(files, File, "old\n"));
		tracker.CloseReview();
		tracker = Tracker(files, persistence);
		var source = differentSource ? Review with { Label = "vs other", MergeBase = "other-base" } : Review;
		string baseline = differentSource ? "different base\n" : "old\n";
		tracker.StartReview(source, ReviewTestChanges.Seed(files, File, baseline));
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
		tracker.StartReview(Review);
		tracker = Tracker(files, persistence);
		Assert.NotNull(tracker.Review);
		tracker.CloseReview();
		tracker = Tracker(files, persistence);
		Assert.Null(tracker.Review);
		tracker.StartReview(Review with { Label = "vs other", MergeBase = "other-base" });
		Assert.Equal("vs other", tracker.Review!.Label);
	}

	[Fact]
	public void Close_RejectedCreationStaysAbsentAndLaterCreationStartsFresh() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "created\n");
		var tracker = Tracker(files, persistence);
		tracker.StartReview(Review, ReviewTestChanges.Seed(files, File, "", false));
		Assert.Equal(RevertHunkOutcome.Deleted, tracker.RevertFile(File));
		tracker.CloseReview();
		tracker = Tracker(files, persistence);
		Assert.Empty(tracker.TurnChanges());
		Assert.False(files.FileExists(File));
		ReviewTestChanges.AgentEdit(tracker, files, File, "recreated\n");
		var recreated = Assert.Single(tracker.TurnChanges());
		Assert.False(recreated.AcceptedBaselineExists);
		Assert.False(recreated.BaselineExists);
		Assert.True(recreated.CurrentExists);
	}
}
