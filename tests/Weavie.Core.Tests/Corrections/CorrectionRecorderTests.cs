using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.Corrections;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests.Corrections;

/// <summary>
/// Exercises the capture path end to end in Core: agent edits recorded by <see cref="SessionChangeTracker"/>,
/// out-of-band user corrections (hand-edits and tracker reverts) drained by <see cref="CorrectionRecorder"/>
/// into a <see cref="CorrectionCorpus"/> at turn boundaries — including the accumulate-model cases (a revert
/// several turns after the write) and the noise gates (kept files, agent-deleted files).
/// </summary>
public sealed class CorrectionRecorderTests {
	private readonly InMemoryFileSystem _fs = new();
	private readonly string _root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";
	private readonly SessionChangeTracker _tracker;
	private readonly CorrectionCorpus _corpus;
	private readonly CorrectionRecorder _recorder;

	public CorrectionRecorderTests() {
		_tracker = new SessionChangeTracker(_fs, _root, _ => true);
		_corpus = new CorrectionCorpus(_fs, Path.Combine(_root, "..", "state", "corrections.jsonl"));
		_recorder = new CorrectionRecorder(_tracker, _corpus);
	}

	[Fact]
	public void HandEditAfterTurn_RecordsDelta_AttributedToProducingPrompt() {
		Boundary("make it fast");
		AgentEdit("app.cs", "agent line\n");
		_fs.WriteAllText(Abs("app.cs"), "user line\n"); // hand-edit: never routed through the tracker

		Boundary("next prompt");

		var record = Assert.Single(_corpus.ReadAll());
		Assert.Equal("make it fast", record.Prompt);
		var file = Assert.Single(record.Files);
		Assert.Equal("app.cs", file.Path);
		Assert.Contains("-agent line", file.Delta, StringComparison.Ordinal);
		Assert.Contains("+user line", file.Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void KeepEverything_RecordsNothing() {
		Boundary("p1");
		AgentEdit("app.cs", "agent line\n");

		Boundary("p2");
		Boundary("p3");

		Assert.Equal(0, _corpus.Count);
	}

	[Fact]
	public void RevertViaTracker_RecordsReversal() {
		_fs.WriteAllText(Abs("app.cs"), "one\n");
		Boundary("p1");
		AgentEdit("app.cs", "one\ntwo\n");

		_tracker.RevertFile(Abs("app.cs"));
		Boundary("p2");

		var file = Assert.Single(Assert.Single(_corpus.ReadAll()).Files);
		Assert.Contains("-two", file.Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void LateRevert_SeveralTurnsAfterTheWrite_StillRecorded() {
		// The review model accumulates: an unreviewed file's snapshot must survive boundaries so a revert made
		// long after the write is still captured.
		_fs.WriteAllText(Abs("app.cs"), "one\n");
		Boundary("p1");
		AgentEdit("app.cs", "one\ntwo\n");
		Boundary("p2");
		Boundary("p3");
		Assert.Equal(0, _corpus.Count);

		_tracker.RevertFile(Abs("app.cs"));
		Boundary("p4");

		var record = Assert.Single(_corpus.ReadAll());
		Assert.Contains("-two", Assert.Single(record.Files).Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void KeptFile_LaterHandEdits_AreNotCorrections() {
		// Keeping ends the correction window: once the user accepts the output, their later edits are just
		// their own coding, not signal.
		Boundary("p1");
		AgentEdit("app.cs", "agent line\n");
		_tracker.KeepFile(Abs("app.cs"));
		Boundary("p2");

		_fs.WriteAllText(Abs("app.cs"), "user rewrite\n");
		Boundary("p3");

		Assert.Equal(0, _corpus.Count);
	}

	[Fact]
	public void KeepThenHandEdit_SameDrainWindow_IsNotACorrection() {
		// The keep accepts the agent's output; a hand-edit made AFTER it (before any intervening boundary) is
		// the user's own follow-up coding, not a correction of the agent — it must not record, even though the
		// stale pre-keep agent snapshot still differs from the hand-edited disk.
		Boundary("p1");
		AgentEdit("app.cs", "agent line\n");
		_tracker.KeepFile(Abs("app.cs"));
		_fs.WriteAllText(Abs("app.cs"), "user rewrite\n");

		Boundary("p2");

		Assert.Equal(0, _corpus.Count);
	}

	[Fact]
	public void HandEditBeforeKeepAll_IsStillRecorded() {
		Boundary("p1");
		AgentEdit("app.cs", "agent line\n");
		_fs.WriteAllText(Abs("app.cs"), "user line\n");
		_tracker.AcceptTurn();

		Boundary("p2");

		Assert.Equal(1, _corpus.Count);
	}

	[Fact]
	public void AgentDeletesItsOwnFile_NotACorrection() {
		Boundary("p1");
		AgentEdit("temp.cs", "scratch\n");
		_fs.DeleteFile(Abs("temp.cs"));
		// The delete reconciles at the agent's next completed tool — agent action, not a user revert.
		_tracker.Observe(new AgentToolCompleted(new AgentMutation.None()));

		Boundary("p2");

		Assert.Equal(0, _corpus.Count);
	}

	[Fact]
	public void UserRevertsCreatedFile_RecordsTheDeletion() {
		Boundary("p1");
		AgentEdit("new.cs", "created\n");

		_tracker.RevertFile(Abs("new.cs")); // created-since-baseline → the revert deletes it
		Boundary("p2");

		var file = Assert.Single(Assert.Single(_corpus.ReadAll()).Files);
		Assert.Equal("new.cs", file.Path);
		Assert.Contains("-created", file.Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void FlushPending_RecordsWithoutWaitingForNextPrompt() {
		Boundary("p1");
		AgentEdit("app.cs", "agent line\n");
		_fs.WriteAllText(Abs("app.cs"), "user line\n");

		_recorder.FlushPending();

		Assert.Equal(1, _corpus.Count);
		Boundary("p2"); // the flushed correction must not report twice at the real boundary
		Assert.Equal(1, _corpus.Count);
	}

	[Fact]
	public void NullPromptBoundary_RecordsWithNullPrompt() {
		Boundary(null); // Codex's turn/started carries no prompt
		AgentEdit("app.cs", "agent line\n");
		_fs.WriteAllText(Abs("app.cs"), "user line\n");

		Boundary(null);

		Assert.Null(Assert.Single(_corpus.ReadAll()).Prompt);
	}

	private void Boundary(string? prompt) {
		var boundary = new AgentPromptSubmitted("session", prompt);
		_tracker.Observe(boundary);
		_recorder.Observe(boundary);
	}

	private void AgentEdit(string relativePath, string content) {
		var mutation = new AgentMutation.File(relativePath, Cwd: null, ProvidesEditLocation: true);
		_tracker.Observe(new AgentToolStarting(mutation));
		_fs.WriteAllText(Abs(relativePath), content);
		_tracker.Observe(new AgentToolCompleted(mutation));
	}

	private string Abs(string relativePath) => Path.Combine(_root, relativePath);
}
