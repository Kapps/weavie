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
		new("github.com", "test", "repo"), Root);
	private static SessionChangeTracker Tracker(InMemoryFileSystem files, IReviewPersistence persistence) =>
		new(files, NoopFileActivitySink.Instance, Root, path => Path.GetDirectoryName(path) == Root, persistence);

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
	public void OverlappingBaseExtension_PreservesTheWholeBoardAndSourceAtomically() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "session baseline\n");
		var tracker = Tracker(files, persistence);
		tracker.CaptureBaseline(File);
		files.WriteAllText(File, "kept proposal\n");
		tracker.RecordChange(File);
		tracker.KeepFile(File);
		var before = tracker.GetTurn(File);
		string? document = persistence.Read();
		string other = Path.Combine(Root, "other.txt");
		files.WriteAllText(other, "new file\n");

		Assert.Throws<InvalidOperationException>(() => tracker.ArmReview(Review, [
			new(other, "", "new file\n", false, true), new(File, "earlier base\n", "kept proposal\n", true, true),
		]));
		Assert.Null(tracker.Review);
		Assert.Null(tracker.GetTurn(other));
		Assert.Equal(before, tracker.GetTurn(File));
		Assert.Equal(document, persistence.Read());
		Assert.Equal("kept proposal\n", files.ReadAllText(File));
	}

	[Fact]
	public void EarlierBaseExtension_AddsCommittedChangesAsPending_WithoutRependingKeptChanges() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "committed\nspace\nold\n");
		var tracker = Tracker(files, persistence);
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
}
