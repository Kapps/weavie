using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Durable decisions resume without rewriting files or stealing later edits.</summary>
public sealed class SessionChangeTrackerPersistenceTests {
	private static readonly string Root = Path.GetFullPath("/w");
	private static readonly string File = Path.Combine(Root, "review.txt");
	private static SessionChangeTracker Tracker(InMemoryFileSystem files, IReviewPersistence persistence) =>
		new(files, NoopFileActivitySink.Instance, Root, path => Path.GetDirectoryName(path) == Root, persistence);

	private static void Edit(SessionChangeTracker tracker, InMemoryFileSystem files, string content) {
		tracker.CaptureBaseline(File);
		files.WriteAllText(File, content);
		tracker.RecordChange(File);
	}

	[Fact]
	public void Restart_ResumesKeptRejectedAndAccumulatedPendingChanges() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "a\nspace1\nb\nspace2\nc\nspace3\nd\n");
		var tracker = Tracker(files, persistence);
		Edit(tracker, files, "A\nspace1\nB\nspace2\nC\nspace3\nd\n");
		Assert.True(tracker.KeepHunk(File, new(1, 2), new(1, 2), "A"));
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertHunk(File, new(3, 4), new(3, 4), "B"));
		tracker.Observe(new AgentPromptSubmitted(null, "Add D"));
		Edit(tracker, files, "A\nspace1\nb\nspace2\nC\nspace3\nD\n");

		var resumed = Tracker(files, persistence);
		var change = Assert.Single(resumed.TurnChanges());
		Assert.Equal("a\nspace1\nb\nspace2\nc\nspace3\nd\n", change.AcceptedBaselineText);
		Assert.Equal("A\nspace1\nb\nspace2\nc\nspace3\nd\n", change.BaselineText);
		Assert.Equal("A\nspace1\nb\nspace2\nC\nspace3\nD\n", change.CurrentText);
		Assert.True(resumed.CanUndoKeep);
		Assert.True(resumed.CanUndoRevert);
		Assert.True(resumed.UndoLastRevert().Acted);
		Assert.Equal("A\nspace1\nB\nspace2\nC\nspace3\nD\n", files.ReadAllText(File));
		Assert.True(resumed.UndoLastKeep().Acted);
		Assert.Equal(change.AcceptedBaselineText, resumed.GetTurn(File)!.BaselineText);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void UndoRejection_TransportsAcrossUnrelatedSameFileEdits_AndPersistsRedo(bool restart) {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "top\nold\nbottom\n");
		var tracker = Tracker(files, persistence);
		Edit(tracker, files, "top\nproposal\nbottom\n");
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertHunk(File, new(2, 3), new(2, 3), "proposal"));
		files.WriteAllText(File, "user prefix\ntop\nold\nuser footer\n");
		if (restart) tracker = Tracker(files, persistence);
		Assert.Equal("user prefix\ntop\nold\nuser footer\n", files.ReadAllText(File));

		Assert.True(tracker.UndoLastRevert().Acted);
		Assert.Equal("user prefix\ntop\nproposal\nuser footer\n", files.ReadAllText(File));
		tracker = Tracker(files, persistence);
		Assert.True(tracker.CanRedo);
		Assert.True(tracker.Redo().Acted);
		Assert.Equal("user prefix\ntop\nold\nuser footer\n", files.ReadAllText(File));
	}

	[Fact]
	public void LaterAgentEditToKeptRegion_IsANewPendingProposalAfterRestart() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "original\n");
		var tracker = Tracker(files, persistence);
		Edit(tracker, files, "kept proposal\n");
		tracker.KeepFile(File);
		tracker.Observe(new AgentPromptSubmitted(null, "Revise that proposal"));
		Edit(tracker, files, "new proposal\n");

		var resumed = Tracker(files, persistence);
		var change = Assert.Single(resumed.TurnChanges());
		Assert.Equal("original\n", change.AcceptedBaselineText);
		Assert.Equal("kept proposal\n", change.BaselineText);
		Assert.Equal("new proposal\n", change.CurrentText);
		Assert.True(resumed.UndoLastKeep().WasBlocked);
		Assert.Equal("new proposal\n", files.ReadAllText(File));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Restart_OverlappingEditBlocksUndo_WithoutOverwritingUserText(bool keep) {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "top\nold\nbottom\n");
		var tracker = Tracker(files, persistence);
		Edit(tracker, files, "top\nproposal\nbottom\n");
		if (keep) Assert.True(tracker.KeepHunk(File, new(2, 3), new(2, 3), "proposal"));
		else Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertHunk(File, new(2, 3), new(2, 3), "proposal"));
		files.WriteAllText(File, "top\nuser replacement\nbottom\n");

		var resumed = Tracker(files, persistence);
		var result = resumed.UndoLast();
		Assert.False(result.Acted);
		Assert.True(result.WasBlocked);
		Assert.Equal("top\nuser replacement\nbottom\n", files.ReadAllText(File));
		Assert.True(Tracker(files, persistence).UndoLast().WasBlocked);
	}

	[Theory]
	[InlineData("new proposal\n")]
	[InlineData("first\r\nsecond\r\n")]
	public void Restart_RejectedCreatedFile_RemainsAbsentUntilUndo_AndCanBeRejectedAgain(string content) {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		var tracker = Tracker(files, persistence);
		Edit(tracker, files, content);
		Assert.Equal(RevertHunkOutcome.Deleted, tracker.RevertFile(File));

		tracker = Tracker(files, persistence);
		Assert.False(files.FileExists(File));
		Assert.True(tracker.CanUndoRevert);
		Assert.True(tracker.UndoLastRevert().Acted);
		Assert.Equal(content, files.ReadAllText(File));
		Assert.True(Tracker(files, persistence).Redo().Acted);
		Assert.False(files.FileExists(File));
	}

	[Fact]
	public void Restart_RecreatedRejectedFile_IsNotOverwrittenByUndo() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		var tracker = Tracker(files, persistence);
		Edit(tracker, files, "new proposal\n");
		Assert.Equal(RevertHunkOutcome.Deleted, tracker.RevertFile(File));
		files.WriteAllText(File, "user created this instead\n");

		var result = Tracker(files, persistence).UndoLastRevert();
		Assert.False(result.Acted);
		Assert.True(result.WasBlocked);
		Assert.Equal("user created this instead\n", files.ReadAllText(File));
	}

	[Fact]
	public void InvalidCheckpoint_FailsWithoutChangingItsDocumentOrFiles() {
		var files = new InMemoryFileSystem();
		files.WriteAllText(File, "user text\n");
		var persistence = new MemoryReviewPersistence();
		persistence.Save("{invalid");

		Assert.Throws<IOException>(() => Tracker(files, persistence));
		Assert.Equal("{invalid", persistence.Read());
		Assert.Equal("user text\n", files.ReadAllText(File));
	}

	[Fact]
	public void TextBecomingBinary_DoesNotPoisonTheCheckpointOrAllowDestructiveUndo() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "old\n");
		var tracker = Tracker(files, persistence);
		Edit(tracker, files, "proposal\n");
		tracker.KeepFile(File);
		tracker.CaptureBaseline(File);
		byte[] binary = [0x50, 0x4b, 0x00, 0xff];
		files.WriteAllBytes(File, binary);
		tracker.RecordChange(File);

		var resumed = Tracker(files, persistence);
		Assert.False(resumed.UndoLast().Acted);
		Assert.Equal(binary, files.ReadAllBytes(File));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void OverlappingRevisionAndRejection_UndoAndRedoInOrder(bool restart) {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "top\nbottom\n");
		var tracker = Tracker(files, persistence);
		Edit(tracker, files, "top\nproposal\nbottom\n");
		Assert.Equal(ReviseApplyOutcome.Applied, tracker.ApplyRevision(File, new(2, 3), "proposal", "revised"));
		Assert.Equal(RevertHunkOutcome.Reverted, tracker.RevertHunk(File, new(2, 2), new(2, 3), "revised"));
		if (restart) tracker = Tracker(files, persistence);

		Assert.True(tracker.UndoLast().Acted);
		Assert.Equal("top\nrevised\nbottom\n", files.ReadAllText(File));
		Assert.True(tracker.UndoLast().Acted);
		Assert.Equal("top\nproposal\nbottom\n", files.ReadAllText(File));
		Assert.True(tracker.Redo().Acted);
		Assert.Equal("top\nrevised\nbottom\n", files.ReadAllText(File));
		Assert.True(tracker.Redo().Acted);
		Assert.Equal("top\nbottom\n", files.ReadAllText(File));
	}

	[Fact]
	public void MalformedNestedOrigins_FailWithoutChangingTheDocumentOrFiles() {
		var files = new InMemoryFileSystem();
		var persistence = new MemoryReviewPersistence();
		files.WriteAllText(File, "old\n");
		var tracker = Tracker(files, persistence);
		Edit(tracker, files, "proposal\n");
		tracker.RevertFile(File);
		var document = JsonNode.Parse(persistence.Read()!)!;
		var patch = document["Undo"]![0]!["Patches"]!.AsArray().First(item => item!["BeforeOrigins"] is not null)!;
		patch["BeforeOrigins"]!["Lines"] = null;
		string corrupted = document.ToJsonString();
		persistence.Save(corrupted);

		Assert.Throws<IOException>(() => Tracker(files, persistence));
		Assert.Equal(corrupted, persistence.Read());
		Assert.Equal("old\n", files.ReadAllText(File));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RetryingKeepAfterSaveFailure_CheckpointsTheDecision(bool all) {
		var files = new InMemoryFileSystem();
		var persistence = new FailingPersistence();
		files.WriteAllText(File, "old\n");
		var tracker = Tracker(files, persistence);
		Edit(tracker, files, "proposal\n");
		persistence.Fail = true;
		void Keep() { if (all) tracker.AcceptTurn(); else tracker.KeepFile(File); }
		Assert.Throws<IOException>(Keep);
		Assert.Equal("old\n", Tracker(files, persistence).GetTurn(File)!.BaselineText);
		persistence.Fail = false;

		Keep();
		var resumed = Tracker(files, persistence);
		Assert.Equal("proposal\n", resumed.GetTurn(File)!.BaselineText);
		Assert.True(resumed.UndoLastKeep().Acted);
		Assert.Equal("old\n", resumed.GetTurn(File)!.BaselineText);
	}

	private sealed class FailingPersistence : IReviewPersistence {
		private readonly MemoryReviewPersistence _memory = new();
		public bool Fail { get; set; }
		public string? Read() => _memory.Read();
		public void Save(string document) {
			if (Fail) throw new IOException("Test checkpoint denied");
			_memory.Save(document);
		}
	}
}
