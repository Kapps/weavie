using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The host→web JSON payloads for the session-changes feed (shared by the Windows + macOS hosts).</summary>
public sealed class ChangeMessagesTests {
	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

	[Fact]
	public void SessionChanges_ListsEachFileWithNameAndCounts() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a\nb\n");
		var tracker = new SessionChangeTracker(fileSystem);
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", "a\nb\nc\n");
		tracker.RecordChange("/w/a.txt");

		var root = Parse(ChangeMessages.SessionChanges(tracker));

		Assert.Equal("session-changes", root.GetProperty("type").GetString());
		var file = Assert.Single(root.GetProperty("files").EnumerateArray());
		Assert.Equal("/w/a.txt", file.GetProperty("path").GetString());
		Assert.Equal("a.txt", file.GetProperty("name").GetString());
		Assert.Equal(1, file.GetProperty("added").GetInt32());
		Assert.Equal(0, file.GetProperty("removed").GetInt32());
	}

	[Fact]
	public void ChangeDiff_CarriesBaselineAndCurrent() {
		var change = new FileChange { Path = "/w/dir/a.cs", BaselineText = "old", CurrentText = "new" };

		var root = Parse(ChangeMessages.ChangeDiff(change));

		Assert.Equal("change-diff", root.GetProperty("type").GetString());
		Assert.Equal("/w/dir/a.cs", root.GetProperty("path").GetString());
		Assert.Equal("a.cs", root.GetProperty("name").GetString());
		Assert.Equal("old", root.GetProperty("baseline").GetString());
		Assert.Equal("new", root.GetProperty("current").GetString());
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
