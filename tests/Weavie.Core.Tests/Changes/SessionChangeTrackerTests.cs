using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The session change tracker fed by the hook stream: baseline at PreToolUse, current at PostToolUse.</summary>
public sealed class SessionChangeTrackerTests {
	private static HookRequest Edit(HookEventKind evt, string path, string? cwd = null) => new() {
		Event = evt,
		ToolName = "Edit",
		ToolInputJson = $$"""{"file_path":{{JsonSerializer.Serialize(path)}}}""",
		Cwd = cwd,
	};

	private static HookRequest EditWithMode(HookEventKind evt, string path, string mode) => new() {
		Event = evt,
		ToolName = "Edit",
		ToolInputJson = $$"""{"file_path":{{JsonSerializer.Serialize(path)}}}""",
		PermissionMode = mode,
	};

	[Fact]
	public void Observe_EditPrePost_RecordsBaselineVsCurrent() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "old\n");
		var tracker = new SessionChangeTracker(fileSystem);

		tracker.Observe(Edit(HookEventKind.PreToolUse, "/w/a.txt"));
		fileSystem.WriteAllText("/w/a.txt", "new\n"); // the edit lands between pre and post
		tracker.Observe(Edit(HookEventKind.PostToolUse, "/w/a.txt"));

		var change = Assert.Single(tracker.Changes());
		Assert.Equal("/w/a.txt", change.Path);
		Assert.Equal("old\n", change.BaselineText);
		Assert.Equal("new\n", change.CurrentText);
	}

	[Fact]
	public void Observe_CreatedFile_BaselineIsEmpty() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = new SessionChangeTracker(fileSystem);

		tracker.Observe(Edit(HookEventKind.PreToolUse, "/w/new.txt")); // absent → baseline ""
		fileSystem.WriteAllText("/w/new.txt", "hello\n");
		tracker.Observe(Edit(HookEventKind.PostToolUse, "/w/new.txt"));

		var change = Assert.Single(tracker.Changes());
		Assert.Equal("", change.BaselineText);
		Assert.Equal("hello\n", change.CurrentText);
	}

	[Fact]
	public void Observe_NonEditingTool_Ignored() {
		var tracker = new SessionChangeTracker(new InMemoryFileSystem());
		var bash = new HookRequest {
			Event = HookEventKind.PreToolUse,
			ToolName = "Bash",
			ToolInputJson = """{"command":"ls"}""",
		};

		tracker.Observe(bash);

		Assert.Empty(tracker.Changes());
	}

	[Fact]
	public void RecordChange_RaisesChanged() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "x");
		var tracker = new SessionChangeTracker(fileSystem);
		int fired = 0;
		tracker.Changed += () => fired++;

		tracker.RecordChange("/w/a.txt");

		Assert.Equal(1, fired);
	}

	[Fact]
	public void TurnChanges_AccumulateAcrossPrompts_UntilAccepted() {
		// The accumulate model: the review baseline is "last reviewed", advancing only on keep-all (AcceptTurn)
		// or a revert — NOT on a new prompt. So a change survives a new prompt and piles up until acknowledged.
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a0\n");
		fileSystem.WriteAllText("/w/b.txt", "b0\n");
		var tracker = new SessionChangeTracker(fileSystem);

		// Turn 1: a.txt a0 -> a1.
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", "a1\n");
		tracker.RecordChange("/w/a.txt");

		// A new prompt arrives — it must NOT reset the review baseline (no turn-scoped clear any more).
		tracker.Observe(new HookRequest {
			Event = HookEventKind.UserPromptSubmit,
			ToolName = string.Empty,
			ToolInputJson = "{}",
		});
		Assert.Single(tracker.TurnChanges()); // a.txt still pending after the new prompt

		// Turn 2: edit b.txt; both files accumulate in the review set.
		tracker.CaptureBaseline("/w/b.txt");
		fileSystem.WriteAllText("/w/b.txt", "b1\n");
		tracker.RecordChange("/w/b.txt");
		Assert.Equal(2, tracker.TurnChanges().Count);

		// a.txt's review diff is still against its ORIGINAL baseline (a0), not reset to a1 by the prompt.
		var aChange = tracker.GetTurn("/w/a.txt");
		Assert.NotNull(aChange);
		Assert.Equal("a0\n", aChange!.BaselineText);
		Assert.Equal("a1\n", aChange.CurrentText);

		// Keep-all clears the whole accumulated set in one action.
		tracker.AcceptTurn();
		Assert.Empty(tracker.TurnChanges());
	}

	[Fact]
	public void AcceptTurn_ClearsTurnDiffButKeepsSessionDiff() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "v0\n");
		var tracker = new SessionChangeTracker(fileSystem);
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", "v1\n");
		tracker.RecordChange("/w/a.txt");

		tracker.AcceptTurn();

		Assert.Empty(tracker.TurnChanges());
		Assert.Single(tracker.Changes()); // session diff (v0 -> v1) remains
	}

	[Fact]
	public void GetTurn_UntouchedThisTurn_ReturnsNull() {
		var tracker = new SessionChangeTracker(new InMemoryFileSystem());
		Assert.Null(tracker.GetTurn("/w/never.txt"));
	}

	[Fact]
	public void RecordChange_RaisesFileChangedWithPath() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "x");
		var tracker = new SessionChangeTracker(fileSystem);
		string? changedPath = null;
		tracker.FileChanged += path => changedPath = path;

		tracker.RecordChange("/w/a.txt");

		Assert.Equal("/w/a.txt", changedPath);
	}

	[Fact]
	public void Observe_CapturesEdits_RegardlessOfPermissionMode() {
		// The tracker is fed by the hook stream, which fires BEFORE the permission check, so an edit is captured
		// whatever mode Claude is in — and a mode flip mid-turn (default → acceptEdits) never drops the edits made
		// under the prior mode. Capture must never depend on the observed mode; only the review SURFACE does.
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a0\n");
		fileSystem.WriteAllText("/w/b.txt", "b0\n");
		var tracker = new SessionChangeTracker(fileSystem);

		// Edit 1 lands while Claude is in default mode (each event carries its permission_mode, which the tracker
		// ignores).
		tracker.Observe(EditWithMode(HookEventKind.PreToolUse, "/w/a.txt", "default"));
		fileSystem.WriteAllText("/w/a.txt", "a1\n");
		tracker.Observe(EditWithMode(HookEventKind.PostToolUse, "/w/a.txt", "default"));

		// The user flips to acceptEdits mid-turn (Shift+Tab); edit 2 auto-applies.
		tracker.Observe(EditWithMode(HookEventKind.PreToolUse, "/w/b.txt", "acceptEdits"));
		fileSystem.WriteAllText("/w/b.txt", "b1\n");
		tracker.Observe(EditWithMode(HookEventKind.PostToolUse, "/w/b.txt", "acceptEdits"));

		// Both edits are captured — in the session diff AND this turn's diff — the mode notwithstanding.
		Assert.Equal(2, tracker.Changes().Count);
		Assert.Equal(2, tracker.TurnChanges().Count);
	}

	[Fact]
	public void EditLocationFor_PostEdit_ReturnsWorkspaceRelativePathAndChangedLine() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/src/a.txt", "one\ntwo\nthree\n");
		var tracker = new SessionChangeTracker(fileSystem);

		tracker.Observe(Edit(HookEventKind.PreToolUse, "/w/src/a.txt", cwd: "/w"));
		fileSystem.WriteAllText("/w/src/a.txt", "one\nTWO\nthree\n"); // line 2 changed
		var post = Edit(HookEventKind.PostToolUse, "/w/src/a.txt", cwd: "/w");
		tracker.Observe(post);

		Assert.Equal("src/a.txt:2", tracker.EditLocationFor(post));
	}

	[Fact]
	public void EditLocationFor_PinpointsThisEdit_NotTheTurnsFirstChange() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "1\n2\n3\n4\n");
		var tracker = new SessionChangeTracker(fileSystem);

		// First edit this turn changes line 1; the turn baseline now sticks at "1\n2\n3\n4\n".
		tracker.Observe(Edit(HookEventKind.PreToolUse, "/w/a.txt", cwd: "/w"));
		fileSystem.WriteAllText("/w/a.txt", "X\n2\n3\n4\n");
		tracker.Observe(Edit(HookEventKind.PostToolUse, "/w/a.txt", cwd: "/w"));

		// Second edit changes line 4 — the per-edit pre-state, not the turn baseline, locates it.
		tracker.Observe(Edit(HookEventKind.PreToolUse, "/w/a.txt", cwd: "/w"));
		fileSystem.WriteAllText("/w/a.txt", "X\n2\n3\nY\n");
		var post = Edit(HookEventKind.PostToolUse, "/w/a.txt", cwd: "/w");
		tracker.Observe(post);

		Assert.Equal("a.txt:4", tracker.EditLocationFor(post));
	}

	[Fact]
	public void EditLocationFor_PreToolUse_ReturnsNull() {
		var tracker = new SessionChangeTracker(new InMemoryFileSystem());
		Assert.Null(tracker.EditLocationFor(Edit(HookEventKind.PreToolUse, "/w/a.txt", cwd: "/w")));
	}

	[Fact]
	public void EditLocationFor_NoNetChange_ReturnsNull() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "same\n");
		var tracker = new SessionChangeTracker(fileSystem);

		tracker.Observe(Edit(HookEventKind.PreToolUse, "/w/a.txt", cwd: "/w"));
		var post = Edit(HookEventKind.PostToolUse, "/w/a.txt", cwd: "/w"); // content unchanged
		tracker.Observe(post);

		Assert.Null(tracker.EditLocationFor(post));
	}

	[Fact]
	public void Changes_NoNetChange_NotReported() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "same");
		var tracker = new SessionChangeTracker(fileSystem);
		tracker.CaptureBaseline("/w/a.txt");
		tracker.RecordChange("/w/a.txt"); // current == baseline

		Assert.Empty(tracker.Changes());
	}

	[Fact]
	public void RevertHunk_MiddleHunk_RestoresThoseLinesLeavingOthersIntact() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a\nb\nc\nd\ne\n");
		var tracker = new SessionChangeTracker(fileSystem);
		tracker.CaptureBaseline("/w/a.txt"); // review baseline = a\nb\nc\nd\ne\n
											 // Claude changes line 2 (b->B) and line 4 (d->D): two hunks separated by the equal line c.
		fileSystem.WriteAllText("/w/a.txt", "a\nB\nc\nD\ne\n");
		tracker.RecordChange("/w/a.txt");

		// Revert ONLY the second hunk (line 4: D -> d). 1-based, end-exclusive ranges; guard = the current line.
		var outcome = tracker.RevertHunk("/w/a.txt", new LineRange(4, 5), new LineRange(4, 5), "D");

		Assert.Equal(RevertHunkOutcome.Reverted, outcome);
		// Exactly line 4 restored; the first hunk's change (B on line 2) is left intact on disk.
		Assert.Equal("a\nB\nc\nd\ne\n", fileSystem.ReadAllText("/w/a.txt"));
		// The review diff now shows only the still-pending first hunk, against the unchanged baseline.
		var change = tracker.GetTurn("/w/a.txt");
		Assert.NotNull(change);
		Assert.Equal("a\nb\nc\nd\ne\n", change!.BaselineText);
		Assert.Equal("a\nB\nc\nd\ne\n", change.CurrentText);
	}

	[Fact]
	public void RevertHunk_GuardMismatch_WritesNothing() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a\nb\n");
		var tracker = new SessionChangeTracker(fileSystem);
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", "a\nB\n");
		tracker.RecordChange("/w/a.txt");

		// A concurrent edit moved the file after the web snapshotted its guard text.
		fileSystem.WriteAllText("/w/a.txt", "a\nXYZ\n");
		var outcome = tracker.RevertHunk("/w/a.txt", new LineRange(2, 3), new LineRange(2, 3), "B");

		Assert.Equal(RevertHunkOutcome.GuardMismatch, outcome);
		Assert.Equal("a\nXYZ\n", fileSystem.ReadAllText("/w/a.txt")); // untouched — no clobber
	}

	[Fact]
	public void RevertHunk_CreatedFile_DeletesIt() {
		var fileSystem = new InMemoryFileSystem();
		var tracker = new SessionChangeTracker(fileSystem);
		tracker.CaptureBaseline("/w/new.txt"); // absent at baseline → created since baseline, baseline ""
		fileSystem.WriteAllText("/w/new.txt", "hello\nworld\n");
		tracker.RecordChange("/w/new.txt");

		// The whole content is one added hunk: an empty baseline range, the current lines as the modified range.
		var outcome = tracker.RevertHunk("/w/new.txt", new LineRange(1, 1), new LineRange(1, 4), "hello\nworld\n");

		Assert.Equal(RevertHunkOutcome.Deleted, outcome);
		Assert.False(fileSystem.FileExists("/w/new.txt")); // deleted, not truncated to a 0-byte file
		Assert.Empty(tracker.TurnChanges()); // dropped from the review set entirely
	}

	[Fact]
	public void RevertFile_CreatedFileDeleted_ExistingRestoredToBaseline() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a0\n");
		var tracker = new SessionChangeTracker(fileSystem);

		// a.txt existed at baseline (a0) and was edited; new.txt was created this session.
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", "a1\n");
		tracker.RecordChange("/w/a.txt");
		tracker.CaptureBaseline("/w/new.txt");
		fileSystem.WriteAllText("/w/new.txt", "created\n");
		tracker.RecordChange("/w/new.txt");

		// Whole-file revert: the existing file returns to its baseline; the created file is deleted (not truncated).
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertFile("/w/a.txt"));
		Assert.Equal(RevertHunkOutcome.Deleted, tracker.RevertFile("/w/new.txt"));

		Assert.Equal("a0\n", fileSystem.ReadAllText("/w/a.txt"));
		Assert.False(fileSystem.FileExists("/w/new.txt"));
		Assert.Empty(tracker.TurnChanges()); // both reverted out of the review set
	}

	[Fact]
	public void RevertHunk_EmptiedExistingFile_TruncatesNotDeletes() {
		// An existing file reverted to an empty baseline is a genuine 0-byte file — NOT a deletion (deletion
		// keys off existence-at-baseline, not emptiness).
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", ""); // existed at baseline, empty
		var tracker = new SessionChangeTracker(fileSystem);
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", "added\n");
		tracker.RecordChange("/w/a.txt");

		var outcome = tracker.RevertHunk("/w/a.txt", new LineRange(1, 1), new LineRange(1, 2), "added");

		Assert.Equal(RevertHunkOutcome.Reverted, outcome);
		Assert.True(fileSystem.FileExists("/w/a.txt"));
		Assert.Equal("", fileSystem.ReadAllText("/w/a.txt"));
	}

	private static HookRequest Bash(HookEventKind evt, string command) => new() {
		Event = evt,
		ToolName = "Bash",
		ToolInputJson = $$"""{"command":{{JsonSerializer.Serialize(command)}}}""",
	};

	[Fact]
	public void Observe_CreatedFileDeletedByBash_DropsFromReviewSet() {
		// Claude creates a file this turn, then removes it with a Bash rm. The deleting tool's PostToolUse
		// reconciles the tracked set against disk: the vanished file leaves the review walk (it can't be opened),
		// raising FileDeleted for it and a single Changed.
		var fileSystem = new InMemoryFileSystem();
		var tracker = new SessionChangeTracker(fileSystem);
		string? deleted = null;
		tracker.FileDeleted += path => deleted = path;

		tracker.Observe(Edit(HookEventKind.PreToolUse, "/w/new.txt")); // absent → created since baseline
		fileSystem.WriteAllText("/w/new.txt", "scratch\n");
		tracker.Observe(Edit(HookEventKind.PostToolUse, "/w/new.txt"));
		Assert.Single(tracker.TurnChanges());

		int changed = 0;
		tracker.Changed += () => changed++;

		// Claude deletes it with a Bash rm; the rm's PostToolUse reconciles it off disk.
		fileSystem.DeleteFile("/w/new.txt");
		tracker.Observe(Bash(HookEventKind.PostToolUse, "rm /w/new.txt"));

		Assert.Equal("/w/new.txt", deleted);
		Assert.Equal(1, changed);                    // exactly one re-push of the review set
		Assert.Empty(tracker.TurnChanges());         // gone from the review walk
		Assert.Empty(tracker.Changes());             // and from the session diff
		Assert.Null(tracker.GetTurn("/w/new.txt"));  // dropped from tracking entirely
	}

	[Fact]
	public void Observe_PostToolUse_NoDeletion_DoesNotFireChanged() {
		// Reconciliation fires events ONLY when something actually vanished — a normal tool call over intact files
		// must not spuriously re-push the review set.
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a0\n");
		var tracker = new SessionChangeTracker(fileSystem);
		tracker.Observe(Edit(HookEventKind.PreToolUse, "/w/a.txt"));
		fileSystem.WriteAllText("/w/a.txt", "a1\n");
		tracker.Observe(Edit(HookEventKind.PostToolUse, "/w/a.txt"));

		int changed = 0;
		string? deleted = null;
		tracker.Changed += () => changed++;
		tracker.FileDeleted += path => deleted = path;

		tracker.Observe(Bash(HookEventKind.PostToolUse, "ls")); // a.txt still on disk

		Assert.Equal(0, changed);
		Assert.Null(deleted);
		Assert.Single(tracker.TurnChanges()); // a.txt still pending
	}

	[Fact]
	public void Observe_ExistingFileDeletedByBash_LeavesReviewSet() {
		// A file that existed at baseline and was edited this turn, then deleted by a Bash rm: it can't be rendered
		// inline (nothing on disk to open), so reconciliation drops it from the review set rather than stranding
		// ← / → on it.
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a0\n");
		var tracker = new SessionChangeTracker(fileSystem);
		string? deleted = null;
		tracker.FileDeleted += path => deleted = path;

		tracker.Observe(Edit(HookEventKind.PreToolUse, "/w/a.txt"));
		fileSystem.WriteAllText("/w/a.txt", "a1\n");
		tracker.Observe(Edit(HookEventKind.PostToolUse, "/w/a.txt"));
		Assert.Single(tracker.TurnChanges());

		fileSystem.DeleteFile("/w/a.txt");
		tracker.Observe(Bash(HookEventKind.PostToolUse, "rm /w/a.txt"));

		Assert.Equal("/w/a.txt", deleted);
		Assert.Empty(tracker.TurnChanges());
		Assert.Empty(tracker.Changes());
	}
}
