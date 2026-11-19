using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>A git review's list and befores are git's answer; see docs/specs/review-state.md.</summary>
public sealed class SessionChangeTrackerReviewSourceTests {
	private static readonly string Root = Path.GetFullPath("/w");
	private static readonly string File = Path.Combine(Root, "review.txt");
	private static readonly string Other = Path.Combine(Root, "other.txt");
	private static readonly ReviewContext Review = new(101, "#101", "base", new("github.com", "test", "repo"), Root, "base", "topic");
	private static readonly ReviewContext Moved = Review with { MergeBase = "moved" };
	private readonly InMemoryFileSystem _files = new();
	private readonly MemoryReviewPersistence _persistence = new();

	private SessionChangeTracker Tracker() =>
		new(_files, NoopFileActivitySink.Instance, Root, path => Path.GetDirectoryName(path) == Root, SessionChangeTracker.NothingIgnored, _persistence);

	private ReviewSeed Seed(string path, string before) => ReviewTestChanges.Seed(_files, path, before);

	// One held file with two pending hunks: "A" (line 1) and "B" (line 3) over "a\nspace\nb\n".
	private SessionChangeTracker Reviewing() {
		_files.WriteAllText(File, "A\nspace\nB\n");
		var tracker = Tracker();
		tracker.StartReview(Review, Seed(File, "a\nspace\nb\n"));
		return tracker;
	}

	[Fact]
	public void AReviewOnAnotherBranchIsAnotherSource() {
		Assert.True(Review.SameSource(Moved));
		Assert.False(Review.SameSource(Review with { Branch = "other" }));
	}

	[Fact]
	public void RetireReview_ClosesOnlyTheReviewItExpects() {
		var tracker = Reviewing();

		Assert.False(tracker.RetireReview(Moved));
		Assert.NotNull(tracker.Review);
		Assert.True(tracker.RetireReview(Review));
		Assert.Null(tracker.Review);
	}

	[Fact]
	public void Replace_ForgetsPathsMissingFromGitsListWithTheirUndo() {
		var tracker = Reviewing();
		_files.WriteAllText(Other, "other\n");
		tracker.StartReview(Review, Seed(File, "a\nspace\nb\n"), Seed(Other, "old other\n"));
		tracker.KeepFile(Other);

		tracker.StartReview(Review, Seed(File, "a\nspace\nb\n"));

		Assert.Null(tracker.GetTurn(Other));
		Assert.Equal(File, Assert.Single(tracker.TurnChanges()).Path);
		Assert.False(tracker.CanUndoKeep);
	}

	[Fact]
	public void Replace_IdenticalBefore_CarriesKeptTextRejectionsAndUndoAcrossARestart() {
		var tracker = Reviewing();
		Assert.True(tracker.KeepHunk(File, new(1, 2), new(1, 2), "A"));
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertHunk(File, new(3, 4), new(3, 4), "B"));
		var decided = tracker.GetTurn(File);

		tracker = Tracker();
		tracker.StartReview(Review, Seed(File, "a\nspace\nb\n"));

		Assert.Equivalent(decided, tracker.GetTurn(File), strict: true);
		Assert.True(tracker.UndoLastRevert().Acted);
		Assert.Equal("A\nspace\nB\n", _files.ReadAllText(File));
		Assert.True(tracker.UndoLastKeep().Acted);
		Assert.Equal("a\nspace\nb\n", tracker.GetTurn(File)!.BaselineText);
	}

	[Fact]
	public void Replace_IdenticalAbsentBefore_KeepsARejectedCreationUndoable() {
		_files.WriteAllText(File, "created\n");
		var tracker = Tracker();
		tracker.StartReview(Review, ReviewTestChanges.Seed(_files, File, "", false));
		Assert.Equal(RevertHunkOutcome.Deleted, tracker.RevertFile(File));
		_files.WriteAllText(Other, "other\n");

		tracker = Tracker();
		tracker.StartReview(Review, ReviewTestChanges.Seed(_files, File, "", false), Seed(Other, ""));

		Assert.False(_files.FileExists(File));
		Assert.Equal("other\n", tracker.GetTurn(Other)!.CurrentText);
		Assert.True(tracker.UndoLastRevert().Acted);
		Assert.Equal("created\n", _files.ReadAllText(File));
	}

	[Fact]
	public void RestoredGitReview_IsNotReconciledAgainstDisk() {
		Reviewing();
		_files.WriteAllText(File, "moved on\n");

		Assert.Equal("A\nspace\nB\n", Tracker().GetTurn(File)!.CurrentText);
	}

	[Theory]
	[InlineData("old\n", true, "older\n", true)]
	[InlineData("", true, "", false)]
	public void Replace_ChangedBefore_StartsThePathOverAtTheNewBefore(
		string before, bool beforeExists, string newBefore, bool newBeforeExists) {
		_files.WriteAllText(File, "new\n");
		var tracker = Tracker();
		tracker.StartReview(Review, ReviewTestChanges.Seed(_files, File, before, beforeExists));
		tracker.KeepFile(File);

		tracker.StartReview(Review, ReviewTestChanges.Seed(_files, File, newBefore, newBeforeExists));

		var change = tracker.GetTurn(File)!;
		Assert.Equal(newBefore, change.BaselineText);
		Assert.Equal(newBeforeExists, change.BaselineExists);
		Assert.False(tracker.CanUndoKeep);
	}

	[Fact]
	public void SupersededRequest_ReturnsNullAndChangesNothing() {
		var tracker = Reviewing();
		_files.WriteAllText(Other, "other\n");
		var stale = tracker.BeginReviewRequest();
		var newest = tracker.BeginReviewRequest();

		Assert.Null(tracker.ReplaceReview(Moved, [Seed(Other, "")], stale));
		Assert.Equal(Review, tracker.Review);
		Assert.Null(tracker.GetTurn(Other));
		Assert.Equal(File, Assert.Single(tracker.TurnChanges()).Path);
		Assert.NotNull(tracker.ReplaceReview(Moved, [Seed(Other, "")], newest));
		Assert.Equal(Moved, tracker.Review);
	}

	[Fact]
	public void BeginRelist_RefusesWhileAUserRequestIsPendingOrTheReviewChanged() {
		var tracker = Reviewing();
		Assert.NotNull(tracker.BeginRelist(Review));

		var started = tracker.BeginReviewRequest();
		Assert.Null(tracker.BeginRelist(Review));
		tracker.EndReviewRequest(started);
		Assert.NotNull(tracker.BeginRelist(Review));
		Assert.Null(tracker.BeginRelist(Moved));
	}

	[Fact]
	public void JoinsRecordedWhileARequestIsPending_AreReturnedByReplace() {
		var tracker = Reviewing();
		var relist = tracker.BeginRelist(Review)!;
		_files.WriteAllText(File, "edited\n");
		_files.WriteAllText(Other, "other\n");
		Assert.True(tracker.RefreshReviewPath(Review, File));
		Assert.True(tracker.JoinReview(Review, Other, "", false));

		var joined = tracker.ReplaceReview(Review, [Seed(File, "a\nspace\nb\n")], relist);

		Assert.Equivalent(new[] { File, Other }, joined);
	}

	[Fact]
	public void JoinReview_RefusesAChangedReviewAndAddsANewPathWithTheGivenBefore() {
		var tracker = Reviewing();
		_files.WriteAllText(Other, "new\n");

		Assert.False(tracker.JoinReview(Moved, Other, "old\n", true));
		Assert.Null(tracker.GetTurn(Other));
		Assert.True(tracker.JoinReview(Review, Other, "old\n", true));
		var joined = tracker.GetTurn(Other)!;
		Assert.Equal("old\n", joined.AcceptedBaselineText);
		Assert.Equal("old\n", joined.BaselineText);
		Assert.Equal("new\n", joined.CurrentText);
	}

	[Fact]
	public void RefreshReviewPath_RefreshesAHeldPathFromDiskAndRefusesAnUnheldOne() {
		var tracker = Reviewing();
		_files.WriteAllText(File, "edited\n");
		_files.WriteAllText(Other, "other\n");

		Assert.False(tracker.RefreshReviewPath(Moved, File));
		Assert.True(tracker.RefreshReviewPath(Review, File));
		Assert.Equal("edited\n", tracker.GetTurn(File)!.CurrentText);
		Assert.False(tracker.RefreshReviewPath(Review, Other));
		Assert.Null(tracker.GetTurn(Other));
	}

	[Fact]
	public void AccountsFor_OnlyWhenEveryPathIsHeldAndDiskEqualsCurrent() {
		var tracker = Reviewing();
		_files.WriteAllText(Other, "other\n");
		Assert.True(tracker.AccountsFor([File]));
		Assert.False(tracker.AccountsFor([File, Other]));

		_files.WriteAllText(File, "edited\n");
		Assert.False(tracker.AccountsFor([File]));
		tracker.RefreshReviewPath(Review, File);
		Assert.True(tracker.AccountsFor([File]));
		_files.DeleteFile(File);
		Assert.False(tracker.AccountsFor([File]));
	}

	[Fact]
	public void Close_ScrapsAndReopeningShowsEverythingPendingAgain() {
		var tracker = Reviewing();
		tracker.KeepFile(File);

		tracker.CloseReview();
		Assert.Null(tracker.Review);
		Assert.Null(tracker.GetTurn(File));
		Assert.False(tracker.CanUndoKeep);
		tracker.StartReview(Review, Seed(File, "a\nspace\nb\n"));

		var reopened = Assert.Single(tracker.TurnChanges());
		Assert.Equal("a\nspace\nb\n", reopened.BaselineText);
		Assert.Equal("A\nspace\nB\n", reopened.CurrentText);
	}

	[Fact]
	public void KeepAll_InAGitReview_LeavesItOpenAndUndoable() {
		var tracker = Reviewing();
		_files.WriteAllText(Other, "other\n");
		tracker.StartReview(Review, Seed(File, "a\nspace\nb\n"), Seed(Other, ""));

		tracker.KeepAll();

		Assert.Equal(Review, tracker.Review);
		Assert.All(tracker.TurnChanges(), change => Assert.Equal(change.CurrentText, change.BaselineText));
		Assert.Equal(2, tracker.TurnChanges().Count);
		Assert.True(tracker.UndoLastKeep().Acted);
		Assert.Equal("a\nspace\nb\n", tracker.GetTurn(File)!.BaselineText);
		Assert.Equal("", tracker.GetTurn(Other)!.BaselineText);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void PendingAgentProvenance_SurvivesCloseSoALaterSaveIsACorrection(bool gitReview) {
		_files.WriteAllText(File, "a\n");
		var tracker = Tracker();
		if (gitReview) tracker.StartReview(Review);
		ReviewTestChanges.AgentEdit(tracker, _files, File, "agent\n");
		if (gitReview) Assert.True(tracker.JoinReview(Review, File, "a\n", true));
		IReadOnlyList<CorrectionEdit>? corrected = null;
		tracker.Corrected += edits => corrected = edits;

		tracker.CloseReview();
		tracker.RecordHandEdit(File, "user\n");

		var edit = Assert.Single(corrected!);
		Assert.Equal("agent", edit.Before);
		Assert.Equal("user", edit.After);
	}

	[Fact]
	public void AnEditUnderWayWhenTheReviewCloses_KeepsTheBeforeItCaptured() {
		var tracker = Reviewing();
		_files.WriteAllText(Other, "original\n");
		tracker.CaptureBaseline(Other);
		_files.WriteAllText(Other, "agent\n");
		tracker.CloseReview();

		tracker.RecordChange(Other);

		var turn = tracker.GetTurn(Other)!;
		Assert.Equal("original\n", turn.AcceptedBaselineText);
		Assert.True(turn.AcceptedBaselineExists);
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertFile(Other));
		Assert.Equal("original\n", _files.ReadAllText(Other));
	}

	[Fact]
	public void AnIgnoredPath_IsRememberedSoItsSavesDontAskGitAgain() {
		_files.WriteAllText(File, "A\nspace\nB\n");
		var tracker = new SessionChangeTracker(_files, NoopFileActivitySink.Instance, Root,
			path => Path.GetDirectoryName(path) == Root, paths => paths.ToHashSet(), _persistence);
		tracker.StartReview(Review, Seed(File, "a\nspace\nb\n"));
		_files.WriteAllText(Other, "ignored\n");
		Assert.False(tracker.RefreshReviewPath(Review, Other));

		Assert.True(tracker.JoinReview(Review, Other, string.Empty, false));

		Assert.True(tracker.RefreshReviewPath(Review, Other));
		Assert.Null(tracker.GetTurn(Other));
	}

	[Fact]
	public void AGitReview_KeepsWhatItShows_AndRefusesToRevertOverAnUnlistedChange() {
		var tracker = Reviewing();
		_files.WriteAllText(File, "A\nspace\nB\nunseen\n");

		Assert.Equal(RevertHunkOutcome.Unlisted, tracker.RevertFile(File));
		Assert.True(tracker.RevertAll().WasBlocked);
		tracker.KeepAll();

		Assert.Equal("A\nspace\nB\nunseen\n", _files.ReadAllText(File));
		Assert.Equal("A\nspace\nB\n", tracker.GetTurn(File)!.BaselineText);
	}
}
