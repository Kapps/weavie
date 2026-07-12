using Weavie.Core.Agents;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests.Sessions;

/// <summary>The structured agent pane's durable subset persists per worktree and reloads safely.</summary>
public sealed class AgentPaneTranscriptStoreTests {
	private const string StorePath = "/weavie-agent-pane-tests/agent-pane.json";

	private static AgentPaneMessage Message(string type, string? status = null, string? itemId = null, string? payload = null) =>
		new() { Type = type, ProviderId = "codex", Status = status, ItemId = itemId, PayloadJson = payload };

	[Theory]
	[InlineData("user-message", null, true)]
	[InlineData("user-steer", null, true)]
	[InlineData("item-completed", null, true)]
	[InlineData("interrupted", null, true)]
	[InlineData("user-image", "submitted", true)]
	[InlineData("user-image", "attached", false)]
	[InlineData("item-started", null, false)]
	[InlineData("turn-started", null, false)]
	[InlineData("turn-diff", null, false)]
	[InlineData("approval-requested", null, false)]
	[InlineData("approval-resolved", null, false)]
	[InlineData("draft", null, false)]
	[InlineData("edit-location", null, false)]
	[InlineData("thread-ready", null, false)]
	// Transient launch/stderr chrome — regenerated live on each launch, so not durable (would re-accumulate).
	[InlineData("warning", null, false)]
	[InlineData("error", null, false)]
	public void IsPersistable_MatchesDurableSubset(string type, string? status, bool expected) =>
		Assert.Equal(expected, AgentPaneTranscriptStore.IsPersistable(Message(type, status)));

	[Fact]
	public void Append_PersistsDurableAndReloads() {
		var fs = new InMemoryFileSystem();
		var store = new AgentPaneTranscriptStore(fs, StorePath);
		store.Append(Message("user-message"));
		store.Append(Message("item-started")); // volatile — dropped
		store.Append(Message("item-completed", status: "completed", itemId: "item-1", payload: """{"a":1}"""));

		var reloaded = new AgentPaneTranscriptStore(fs, StorePath).Snapshot();

		Assert.Equal(["user-message", "item-completed"], reloaded.Select(m => m.Type));
		Assert.Equal("""{"a":1}""", reloaded[1].PayloadJson); // raw payload survives verbatim
		Assert.Equal("item-1", reloaded[1].ItemId);
	}

	[Fact]
	public void VolatileMessages_AreNotPersisted() {
		var fs = new InMemoryFileSystem();
		var store = new AgentPaneTranscriptStore(fs, StorePath);
		store.Append(Message("item-started"));
		store.Append(Message("approval-requested"));
		store.Append(Message("draft"));

		Assert.Empty(store.Snapshot());
		Assert.False(fs.FileExists(StorePath)); // nothing durable ⇒ nothing written
	}

	[Fact]
	public void Clear_EmptiesAndRemovesFile() {
		var fs = new InMemoryFileSystem();
		var store = new AgentPaneTranscriptStore(fs, StorePath);
		store.Append(Message("user-message"));
		Assert.True(fs.FileExists(StorePath));

		store.Clear();

		Assert.Empty(store.Snapshot());
		Assert.False(fs.FileExists(StorePath));
	}

	[Fact]
	public void MalformedLine_IsSkipped_KeepingValidRecords() {
		var fs = new InMemoryFileSystem();
		var store = new AgentPaneTranscriptStore(fs, StorePath);
		store.Append(Message("user-message"));
		// A torn line (a crash mid-append) or stale-schema record: appended raw, must not discard the good line.
		fs.AppendAllText(StorePath, "{ broken\n");
		store.Append(Message("item-completed", itemId: "item-1"));

		var reloaded = new AgentPaneTranscriptStore(fs, StorePath).Snapshot();

		Assert.Equal(["user-message", "item-completed"], reloaded.Select(m => m.Type));
	}
}
