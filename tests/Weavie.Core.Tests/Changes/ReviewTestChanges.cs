using Weavie.Core.Changes;
using Weavie.Core.FileSystem;
using Weavie.Core.Review;

namespace Weavie.Core.Tests;

internal static class ReviewTestChanges {
	internal static void AddPendingFile(SessionChangeTracker tracker, InMemoryFileSystem files, string path) =>
		AgentEdit(tracker, files, path, "pending\n");

	internal static void AgentEdit(SessionChangeTracker tracker, InMemoryFileSystem files, string path, string content) {
		tracker.CaptureBaseline(path);
		files.WriteAllText(path, content);
		tracker.RecordChange(path);
	}

	internal static ReviewContext GitReview(string root) => new(0, "vs main", "base", null, root, "main", "topic");

	// A git listing's seed for a path whose current side is whatever the test already wrote to disk.
	internal static ReviewSeed Seed(InMemoryFileSystem files, string path, string before, bool beforeExists) =>
		new(path, before, files.FileExists(path) ? files.ReadAllText(path) : string.Empty, beforeExists, files.FileExists(path));

	internal static ReviewSeed Seed(InMemoryFileSystem files, string path, string before) => Seed(files, path, before, true);

	internal static IReadOnlyCollection<string>? StartReview(
		this SessionChangeTracker tracker, ReviewContext review, params ReviewSeed[] seeds) =>
		tracker.ReplaceReview(review, seeds, tracker.BeginReviewRequest());

	// Most tests act on the review baseline they just set up; the baseline-revision guard has tests of its own.
	internal static bool KeepHunk(
		this SessionChangeTracker tracker, string path, LineRange baselineRange, LineRange currentRange, string guardText) =>
		tracker.KeepHunk(path, BaselineRevision(tracker, path), baselineRange, currentRange, guardText);

	internal static RevertHunkOutcome RevertHunk(
		this SessionChangeTracker tracker, string path, LineRange baselineRange, LineRange currentRange, string guardText) =>
		tracker.RevertHunk(path, BaselineRevision(tracker, path), baselineRange, currentRange, guardText);

	internal static string? BaselineRevision(SessionChangeTracker tracker, string path) =>
		tracker.GetTurn(path) is { } turn ? TextRevision.Of(turn.BaselineText, turn.BaselineExists) : null;
}
