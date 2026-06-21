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
	public void TurnChanges_TracksOnlyThisTurnAgainstTurnBaseline() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "v0\n");
		var tracker = new SessionChangeTracker(fileSystem);

		// Turn 1: a.txt v0 -> v1.
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", "v1\n");
		tracker.RecordChange("/w/a.txt");

		var turn1 = Assert.Single(tracker.TurnChanges());
		Assert.Equal("v0\n", turn1.BaselineText);
		Assert.Equal("v1\n", turn1.CurrentText);

		// New turn: prior changes implicitly accepted -> turn baseline resets to current (v1).
		tracker.BeginTurn();
		Assert.Empty(tracker.TurnChanges());

		// Turn 2: a.txt v1 -> v2. The turn diff is v1->v2, NOT the session v0->v2.
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", "v2\n");
		tracker.RecordChange("/w/a.txt");

		var turn2 = Assert.Single(tracker.TurnChanges());
		Assert.Equal("v1\n", turn2.BaselineText);
		Assert.Equal("v2\n", turn2.CurrentText);
		// Session diff still spans the whole session.
		Assert.Equal("v0\n", Assert.Single(tracker.Changes()).BaselineText);
	}

	[Fact]
	public void BeginTurn_RaisesTurnBegan() {
		var tracker = new SessionChangeTracker(new InMemoryFileSystem());
		int fired = 0;
		tracker.TurnBegan += () => fired++;

		tracker.Observe(new HookRequest {
			Event = HookEventKind.UserPromptSubmit,
			ToolName = string.Empty,
			ToolInputJson = "{}",
		});

		Assert.Equal(1, fired);
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
}
