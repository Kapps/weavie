using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Feature payloads for the inline turn-review feed.</summary>
public sealed class ChangeMessagesTests {
	private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

	[Fact]
	public void TurnChanges_ListsChangedFilesWithCountsAndFirstChangeLine() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a\n");
		// CaptureBaseline/RecordChange are called directly here (not via Observe), so scope is moot; accept all.
		var tracker = new SessionChangeTracker(fileSystem, NoopFileActivitySink.Instance, "/w", _ => true);
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", "a\nb\n");
		tracker.RecordChange("/w/a.txt");

		var root = Parse(ChangeMessages.TurnChanges(tracker, "vs main"));

		Assert.Equal("vs main", root.GetProperty("label").GetString()); // names an armed PR/ref review; empty for a plain turn
		var file = Assert.Single(root.GetProperty("files").EnumerateArray());
		Assert.Equal("/w/a.txt", file.GetProperty("path").GetString());
		Assert.Equal(1, file.GetProperty("added").GetInt32());
		Assert.Equal(0, file.GetProperty("removed").GetInt32());
		Assert.Equal(2, file.GetProperty("line").GetInt32()); // the appended line is the navigator's jump target
		Assert.True(file.GetProperty("currentExists").GetBoolean());
	}

	[Fact]
	public void TurnDiff_CarriesTheAcceptedReviewCurrentTriple() {
		var change = new FileChange {
			Path = "/w/dir/a.cs",
			AcceptedBaselineText = "anchor",
			AcceptedBaselineExists = true,
			BaselineText = "before",
			BaselineExists = true,
			CurrentText = "after",
			CurrentExists = true,
		};

		var root = Parse(ChangeMessages.TurnDiff(change));

		Assert.Equal("/w/dir/a.cs", root.GetProperty("path").GetString());
		Assert.Equal("a.cs", root.GetProperty("name").GetString());
		Assert.Equal("anchor", root.GetProperty("acceptedBaseline").GetString()); // faded band origin
		Assert.True(root.GetProperty("acceptedBaselineExists").GetBoolean());
		Assert.Equal("before", root.GetProperty("baseline").GetString());         // bright band origin (review baseline)
		Assert.True(root.GetProperty("baselineExists").GetBoolean());
		Assert.Equal("after", root.GetProperty("current").GetString());
		Assert.True(root.GetProperty("currentExists").GetBoolean());
	}

	[Fact]
	public void TurnDiff_OmitsAcceptedAnchorWhenItMatchesTheBaseline() {
		// Until anything is kept, the anchor equals the baseline: sending it again would double the wire cost of
		// every file in the turn for no new information (a real defect for a turn touching many large files — see
		// WebSocketHostBridge's per-connection outbox). The client already treats a missing anchor as "== baseline".
		var change = new FileChange {
			Path = "/w/dir/a.cs",
			AcceptedBaselineText = "before",
			AcceptedBaselineExists = true,
			BaselineText = "before",
			BaselineExists = true,
			CurrentText = "after",
			CurrentExists = true,
		};

		var root = Parse(ChangeMessages.TurnDiff(change));

		Assert.Equal(JsonValueKind.Null, root.GetProperty("acceptedBaseline").ValueKind);
		Assert.Equal(JsonValueKind.Null, root.GetProperty("acceptedBaselineExists").ValueKind);
		Assert.Equal("before", root.GetProperty("baseline").GetString());
	}

	[Fact]
	public void TurnReset_IsEmptyPayload() {
		var root = Parse(ChangeMessages.TurnReset());
		Assert.Empty(root.EnumerateObject());
	}
}
