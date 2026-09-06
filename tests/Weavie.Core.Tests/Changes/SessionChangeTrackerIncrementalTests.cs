using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class SessionChangeTrackerIncrementalTests {
	private static readonly string Root = Path.GetFullPath("/w");
	private static readonly string Target = Path.Combine(Root, "target.txt");
	private static SessionChangeTracker Tracker(InMemoryFileSystem files, IReviewPersistence persistence) =>
		new(files, NoopFileActivitySink.Instance, Root, path => Path.GetDirectoryName(path) == Root, persistence);

	[Theory]
	[InlineData(0)]
	[InlineData(64)]
	public void EditingOneFile_DoesNotSerializeUnrelatedFilesHistoryOrPrompts(int unrelated) {
		var files = new InMemoryFileSystem();
		var persistence = new RecordingPersistence();
		var tracker = Tracker(files, persistence);
		for (int i = 0; i < unrelated; i++) {
			string path = Path.Combine(Root, $"other-{i}.txt");
			tracker.Observe(new AgentConversationEvent($"side-{i}", new AgentPromptSubmitted(null, new string('p', 1000))));
			files.WriteAllText(path, "original\n");
			tracker.CaptureBaseline(path);
			files.WriteAllText(path, string.Join('\n', Enumerable.Repeat("proposal", 100)));
			tracker.RecordChange(path);
			tracker.KeepFile(path);
		}
		files.WriteAllText(Target, "original\n");
		tracker.CaptureBaseline(Target);
		persistence.Writes.Clear();

		files.WriteAllText(Target, "edited\n");
		tracker.RecordChange(Target);

		var write = Assert.Single(persistence.Writes);
		Assert.Equal(2, write.Count);
		Assert.True(write.ContainsKey("metadata"));
		var file = Assert.Single(write, pair => pair.Key.StartsWith("file:", StringComparison.Ordinal));
		Assert.Equal(Target, JsonNode.Parse(file.Value!)!["Path"]!.GetValue<string>());
		Assert.Equal("edited\n", Tracker(files, persistence).GetTurn(Target)!.CurrentText);
	}

	[Theory]
	[InlineData(1)]
	[InlineData(64)]
	public void EditingOneFileInKeepAll_UpdatesOnlyItsPatchGroup(int fileCount) {
		var files = new InMemoryFileSystem();
		var persistence = new RecordingPersistence();
		var tracker = Tracker(files, persistence);
		string[] paths = [.. Enumerable.Range(0, fileCount).Select(i => Path.Combine(Root, $"group-{i}.txt"))];
		foreach (string path in paths) {
			files.WriteAllText(path, "old\n");
			tracker.CaptureBaseline(path);
			files.WriteAllText(path, "proposal\n");
			tracker.RecordChange(path);
		}
		tracker.AcceptTurn();
		persistence.Writes.Clear();
		files.WriteAllText(paths[0], "prefix\nproposal\n");
		tracker.RecordHandEdit(paths[0], files.ReadAllText(paths[0]));

		var write = Assert.Single(persistence.Writes);
		Assert.Equal(2, write.Count);
		var group = Assert.Single(write, pair => pair.Key.StartsWith("history-patches:", StringComparison.Ordinal));
		Assert.Equal(paths[0], JsonNode.Parse(group.Value!)!["Path"]!.GetValue<string>());
		Assert.True(Tracker(files, persistence).UndoLastKeep().Acted);
		Assert.Equal("prefix\nproposal\n", files.ReadAllText(paths[0]));
	}

	[Fact]
	public void FailedSave_RetainsDirtyFilesAndDoesNotTransportHistoryTwice() {
		var files = new InMemoryFileSystem();
		var persistence = new RecordingPersistence();
		var tracker = Tracker(files, persistence);
		files.WriteAllText(Target, "top\nold\nbottom\n");
		tracker.CaptureBaseline(Target);
		files.WriteAllText(Target, "top\nproposal\nbottom\n");
		tracker.RecordChange(Target);
		tracker.RevertHunk(Target, new(2, 3), new(2, 3), "proposal");
		var committed = persistence.Read();
		files.WriteAllText(Target, "prefix\ntop\nold\nbottom\n");
		persistence.Fail = true;
		Assert.Throws<IOException>(() => tracker.RecordHandEdit(Target, files.ReadAllText(Target)));
		Assert.Equal(committed, persistence.Read());
		persistence.Fail = false;

		string other = Path.Combine(Root, "other.txt");
		tracker.CaptureBaseline(other);
		var resumed = Tracker(files, persistence);
		Assert.True(resumed.UndoLastRevert().Acted);
		Assert.Equal("prefix\ntop\nproposal\nbottom\n", files.ReadAllText(Target));
	}

	[Fact]
	public void EveryMultiFileUndoCheckpoint_RestoresBothHistoryEntries() {
		var files = new InMemoryFileSystem();
		var persistence = new RecordingPersistence();
		var tracker = Tracker(files, persistence);
		string other = Path.Combine(Root, "other.txt");
		foreach (string path in new[] { Target, other }) {
			files.WriteAllText(path, "old\n");
			tracker.CaptureBaseline(path);
			files.WriteAllText(path, "proposal\n");
			tracker.RecordChange(path);
		}
		tracker.AcceptTurn();
		var before = persistence.Read();
		persistence.Writes.Clear();
		Assert.True(tracker.UndoLastKeep().Acted);
		Assert.Equal(2, persistence.Writes.Count);

		var intermediate = new MemoryReviewPersistence();
		intermediate.Save(before.ToDictionary(pair => pair.Key, pair => (string?)pair.Value));
		intermediate.Save(persistence.Writes[0]);
		var resumed = Tracker(files, intermediate);
		Assert.True(resumed.CanUndoKeep);
		Assert.True(resumed.CanRedo);
		Assert.Equal("old\n", resumed.GetTurn(Target)!.BaselineText);
		Assert.Equal("proposal\n", resumed.GetTurn(other)!.BaselineText);
		Assert.True(resumed.Redo().Acted);
		Assert.Equal("proposal\n", resumed.GetTurn(Target)!.BaselineText);
		Assert.True(Tracker(files, intermediate).UndoLastKeep().Acted);
	}

	private sealed class RecordingPersistence : IReviewPersistence {
		private readonly MemoryReviewPersistence _memory = new();
		public List<IReadOnlyDictionary<string, string?>> Writes { get; } = [];
		public bool Fail { get; set; }
		public IReadOnlyDictionary<string, string> Read() => _memory.Read();
		public void Save(IReadOnlyDictionary<string, string?> changes) {
			if (Fail) throw new IOException("Test transaction rejected");
			_memory.Save(changes);
			Writes.Add(new Dictionary<string, string?>(changes));
		}
	}
}
