using Weavie.Core.Changes;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Tests;

internal static class ReviewTestChanges {
	internal static void AddPendingFile(SessionChangeTracker tracker, InMemoryFileSystem files, string path) {
		tracker.CaptureBaseline(path);
		files.WriteAllText(path, "pending\n");
		tracker.RecordChange(path);
	}

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
