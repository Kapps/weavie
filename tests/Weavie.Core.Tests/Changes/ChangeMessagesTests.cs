using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Host→web JSON payloads for the inline turn-review feed.</summary>
public sealed class ChangeMessagesTests {
	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

	[Fact]
	public void TurnChanges_ListsChangedFilesWithCountsAndFirstChangeLine() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a\n");
		// CaptureBaseline/RecordChange are called directly here (not via Observe), so scope is moot; accept all.
		var tracker = new SessionChangeTracker(fileSystem, _ => true);
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", "a\nb\n");
		tracker.RecordChange("/w/a.txt");

		var root = Parse(ChangeMessages.TurnChanges(tracker));

		Assert.Equal("turn-changes", root.GetProperty("type").GetString());
		var file = Assert.Single(root.GetProperty("files").EnumerateArray());
		Assert.Equal("/w/a.txt", file.GetProperty("path").GetString());
		Assert.Equal(1, file.GetProperty("added").GetInt32());
		Assert.Equal(0, file.GetProperty("removed").GetInt32());
		Assert.Equal(2, file.GetProperty("line").GetInt32()); // the appended line is the navigator's jump target
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
