using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Guarded in-place revision of an agent-written region: the write lands only while the file still holds the text
/// the region was captured from, and it never disturbs the review baseline.
/// </summary>
public sealed class SessionChangeTrackerRevisionTests {
	private static SessionChangeTracker Tracker(IFileSystem fileSystem) =>
		new(fileSystem, NoopFileActivitySink.Instance, "/w", path => path.StartsWith("/w", StringComparison.Ordinal));

	// Stages an agent edit: `baseline` captured at PreToolUse, `written` recorded at PostToolUse.
	private static SessionChangeTracker Staged(IFileSystem fileSystem, string baseline, string written) {
		fileSystem.WriteAllText("/w/a.cs", baseline);
		var tracker = Tracker(fileSystem);
		tracker.CaptureBaseline("/w/a.cs");
		fileSystem.WriteAllText("/w/a.cs", written);
		tracker.RecordChange("/w/a.cs");
		return tracker;
	}

	[Fact]
	public void ApplyRevision_GuardMatches_ReplacesRegionOnDisk() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Staged(fileSystem, "code\n", "// one\n// two\n// three\ncode\n");

		var outcome = tracker.ApplyRevision("/w/a.cs", new LineRange(1, 4), "// one\n// two\n// three", "// short");

		Assert.Equal(ReviseApplyOutcome.Applied, outcome);
		Assert.Equal("// short\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
	}

	[Fact]
	public void ApplyRevision_GuardMismatch_WritesNothing() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Staged(fileSystem, "code\n", "// one\n// two\n// three\ncode\n");
		// The user retypes the region while the query is in flight; the captured text no longer matches.
		fileSystem.WriteAllText("/w/a.cs", "// mine\n// two\n// three\ncode\n");

		var outcome = tracker.ApplyRevision("/w/a.cs", new LineRange(1, 4), "// one\n// two\n// three", "// short");

		Assert.Equal(ReviseApplyOutcome.GuardMismatch, outcome);
		Assert.Equal("// mine\n// two\n// three\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
	}

	[Fact]
	public void ApplyRevision_LinesShiftedAbove_AbortsRatherThanRelocating() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Staged(fileSystem, "code\n", "// one\n// two\n// three\ncode\n");
		// A line inserted above moves the region; the range now spans different text, so the guard must abort.
		fileSystem.WriteAllText("/w/a.cs", "using X;\n// one\n// two\n// three\ncode\n");

		var outcome = tracker.ApplyRevision("/w/a.cs", new LineRange(1, 4), "// one\n// two\n// three", "// short");

		Assert.Equal(ReviseApplyOutcome.GuardMismatch, outcome);
		Assert.Equal("using X;\n// one\n// two\n// three\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
	}

	[Fact]
	public void ApplyRevision_LeavesBaselineSoReviewShowsOneHunk() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Staged(fileSystem, "code\n", "// one\n// two\n// three\ncode\n");

		tracker.ApplyRevision("/w/a.cs", new LineRange(1, 4), "// one\n// two\n// three", "// short");

		var change = tracker.GetTurn("/w/a.cs");
		Assert.NotNull(change);
		// The user reviews baseline -> revised, never the agent's pre-revision text as a second change.
		Assert.Equal("code\n", change!.BaselineText);
		Assert.Equal("// short\ncode\n", change.CurrentText);
	}

	[Fact]
	public void ApplyRevision_CrlfFile_KeepsCrlfEndings() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Staged(fileSystem, "code\r\n", "// one\r\n// two\r\n// three\r\ncode\r\n");

		// Guard text is LF-joined the way a Monaco model reports it; the write keeps the file's CRLF endings.
		var outcome = tracker.ApplyRevision("/w/a.cs", new LineRange(1, 4), "// one\n// two\n// three", "// short");

		Assert.Equal(ReviseApplyOutcome.Applied, outcome);
		Assert.Equal("// short\r\ncode\r\n", fileSystem.ReadAllText("/w/a.cs"));
	}

	[Fact]
	public void ApplyRevision_UndoLast_RestoresPreRevisionText() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Staged(fileSystem, "code\n", "// one\n// two\n// three\ncode\n");
		tracker.ApplyRevision("/w/a.cs", new LineRange(1, 4), "// one\n// two\n// three", "// short");

		var result = tracker.UndoLast();

		Assert.True(result.Acted);
		Assert.Equal("// one\n// two\n// three\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
	}

	[Fact]
	public void ApplyRevision_UndoLastRevert_IgnoresIt() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Staged(fileSystem, "code\n", "// one\n// two\n// three\ncode\n");
		tracker.ApplyRevision("/w/a.cs", new LineRange(1, 4), "// one\n// two\n// three", "// short");

		// A revision is neither a keep nor a revert, so the type-split chords must not consume it.
		Assert.False(tracker.UndoLastRevert().Acted);
		Assert.False(tracker.UndoLastKeep().Acted);
		Assert.Equal("// short\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
	}

	[Fact]
	public void ApplyRevision_OutOfBoundsRange_WritesNothing() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = Staged(fileSystem, "code\n", "// one\ncode\n");

		var outcome = tracker.ApplyRevision("/w/a.cs", new LineRange(1, 9), "// one\ncode\n", "// short");

		Assert.Equal(ReviseApplyOutcome.GuardMismatch, outcome);
		Assert.Equal("// one\ncode\n", fileSystem.ReadAllText("/w/a.cs"));
	}
}
