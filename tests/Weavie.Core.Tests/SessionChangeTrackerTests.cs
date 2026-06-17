using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The session change tracker fed by the hook stream: baseline at PreToolUse, current at PostToolUse.</summary>
public sealed class SessionChangeTrackerTests {
	private static HookRequest Edit(HookEventKind evt, string path) => new() {
		Event = evt,
		ToolName = "Edit",
		ToolInputJson = $$"""{"file_path":{{JsonSerializer.Serialize(path)}}}""",
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
			Event = HookEventKind.PreToolUse, ToolName = "Bash", ToolInputJson = """{"command":"ls"}""",
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
	public void Summarize_CountsAddedAndRemoved() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a\nb\nc\n");
		var tracker = new SessionChangeTracker(fileSystem);
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", "a\nc\nd\n");
		tracker.RecordChange("/w/a.txt");

		var summary = Assert.Single(tracker.Summarize());
		Assert.Equal("/w/a.txt", summary.Path);
		Assert.Equal(1, summary.Added);   // "d"
		Assert.Equal(1, summary.Removed); // "b"
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
