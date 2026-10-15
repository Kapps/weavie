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
		Assert.Equal(1, file.GetProperty("pendingLines").GetInt32());
		Assert.Equal(2, file.GetProperty("line").GetInt32()); // the appended line is the navigator's jump target
		Assert.True(file.GetProperty("currentExists").GetBoolean());
	}

	[Fact]
	public void TurnDiff_CarriesTheAcceptedReviewCurrentTripleAsRevisions() {
		var change = new FileChange {
			Path = "/w/dir/a.cs",
			AcceptedBaselineText = "anchor",
			AcceptedBaselineExists = true,
			BaselineText = "before",
			BaselineExists = false,
			CurrentText = "after",
			CurrentExists = true,
		};

		var root = Parse(ChangeMessages.TurnDiff(change));

		Assert.Equal("/w/dir/a.cs", root.GetProperty("path").GetString());
		Assert.Equal("a.cs", root.GetProperty("name").GetString());
		Assert.Equal(TextRevision.Of("anchor"), root.GetProperty("acceptedBaselineRevision").GetString()); // faded band origin
		Assert.True(root.GetProperty("acceptedBaselineExists").GetBoolean());
		Assert.Equal(TextRevision.Of("before"), root.GetProperty("baselineRevision").GetString());         // bright band origin
		Assert.False(root.GetProperty("baselineExists").GetBoolean());
		Assert.Equal(TextRevision.Of("after"), root.GetProperty("revision").GetString());
		Assert.True(root.GetProperty("currentExists").GetBoolean());
	}

	[Fact]
	public void TurnDiff_OfAMegabyteFile_CarriesNoText() {
		string text = new('x', 1024 * 1024);
		var change = new FileChange {
			Path = "/w/big.txt",
			AcceptedBaselineText = text,
			BaselineText = text + "y",
			CurrentText = text + "z",
			BaselineExists = true,
			CurrentExists = true,
		};

		Assert.True(ChangeMessages.TurnDiff(change).Length < 1024);
	}

	[Fact]
	public void ReviewText_ServesBothBaselinesUntilSuperseded() {
		var fileSystem = new InMemoryFileSystem();
		fileSystem.WriteAllText("/w/a.txt", "a\nb\nc\nd\n");
		var tracker = new SessionChangeTracker(fileSystem, NoopFileActivitySink.Instance, "/w", _ => true);
		tracker.CaptureBaseline("/w/a.txt");
		fileSystem.WriteAllText("/w/a.txt", "a\nB\nc\nD\n");
		tracker.RecordChange("/w/a.txt");
		string anchor = TextRevision.Of("a\nb\nc\nd\n");
		Assert.Equal("a\nb\nc\nd\n", tracker.ReviewText("/w/a.txt", anchor));

		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(2, 3), new LineRange(2, 3), "B"));
		string kept = TextRevision.Of("a\nB\nc\nd\n");
		Assert.Equal("a\nb\nc\nd\n", tracker.ReviewText("/w/a.txt", anchor));
		Assert.Equal("a\nB\nc\nd\n", tracker.ReviewText("/w/a.txt", kept));

		Assert.True(tracker.KeepHunk("/w/a.txt", new LineRange(4, 5), new LineRange(4, 5), "D"));
		Assert.Null(tracker.ReviewText("/w/a.txt", kept));
		Assert.Null(tracker.ReviewText("/w/other.txt", anchor));
	}

	[Fact]
	public void TurnReset_IsEmptyPayload() {
		var root = Parse(ChangeMessages.TurnReset());
		Assert.Empty(root.EnumerateObject());
	}
}
