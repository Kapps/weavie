using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Host→web JSON payloads for the inline turn-review feed.</summary>
public sealed class ChangeMessagesTests {
	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

	[Fact]
	public void EmptyTurnChanges_IsTypeWithEmptyFilesAndNeverOpens() {
		var root = Parse(ChangeMessages.EmptyTurnChanges());

		Assert.Equal("turn-changes", root.GetProperty("type").GetString());
		Assert.Empty(root.GetProperty("files").EnumerateArray());
		Assert.False(root.GetProperty("open").GetBoolean());
	}

	[Fact]
	public void TurnChanges_ListsFilesAndCarriesOpenFlag() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a\n");
		var tracker = new SessionChangeTracker(fileSystem);
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", "a\nb\n");
		tracker.RecordChange("/w/a.txt");

		var root = Parse(ChangeMessages.TurnChanges(tracker, open: true));

		Assert.Equal("turn-changes", root.GetProperty("type").GetString());
		Assert.True(root.GetProperty("open").GetBoolean());
		var file = Assert.Single(root.GetProperty("files").EnumerateArray());
		Assert.Equal("/w/a.txt", file.GetProperty("path").GetString());
		Assert.Equal(1, file.GetProperty("added").GetInt32());
	}

	[Fact]
	public void TurnDiff_CarriesBaselineAndCurrent() {
		var change = new FileChange { Path = "/w/dir/a.cs", BaselineText = "before", CurrentText = "after" };

		var root = Parse(ChangeMessages.TurnDiff(change));

		Assert.Equal("turn-diff", root.GetProperty("type").GetString());
		Assert.Equal("/w/dir/a.cs", root.GetProperty("path").GetString());
		Assert.Equal("a.cs", root.GetProperty("name").GetString());
		Assert.Equal("before", root.GetProperty("baseline").GetString());
		Assert.Equal("after", root.GetProperty("current").GetString());
	}

	[Fact]
	public void TurnReset_IsTypeOnly() {
		var root = Parse(ChangeMessages.TurnReset());
		Assert.Equal("turn-reset", root.GetProperty("type").GetString());
	}
}
