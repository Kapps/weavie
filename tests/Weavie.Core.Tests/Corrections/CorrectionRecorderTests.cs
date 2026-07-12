using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.Corrections;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Core.Tests.Corrections;

/// <summary>
/// Exercises the event-driven capture path in Core: agent edits recorded by <see cref="SessionChangeTracker"/>,
/// then the user's corrections — an editor save over an agent hunk (<see cref="SessionChangeTracker.RecordHandEdit"/>)
/// or a review-UI revert — raise <see cref="SessionChangeTracker.Corrected"/>, which the
/// <see cref="CorrectionRecorder"/> appends to a <see cref="CorrectionCorpus"/> at the moment they act. The
/// gate keeps the user's edits to agent-untouched regions (their own coding, which autosave fires on
/// repeatedly) out of the corpus.
/// </summary>
public sealed class CorrectionRecorderTests {
	private readonly InMemoryFileSystem _fs = new();
	private readonly string _root = OperatingSystem.IsWindows() ? @"C:\repo" : "/repo";
	private readonly SessionChangeTracker _tracker;
	private readonly CorrectionCorpus _corpus;

	public CorrectionRecorderTests() {
		_tracker = new SessionChangeTracker(_fs, _root, _ => true);
		_corpus = new CorrectionCorpus(_fs, Path.Combine(_root, "..", "state", "corrections.jsonl"));
		_tracker.Corrected += new CorrectionRecorder(_corpus).Record;
	}

	[Fact]
	public void HandEditOverAgentHunk_RecordsDelta_AttributedToProducingPrompt() {
		Boundary("make it fast");
		AgentEdit("app.cs", "agent line\n");
		HandEdit("app.cs", "user line\n");

		var record = Assert.Single(_corpus.ReadAll());
		Assert.Equal("make it fast", record.Prompt);
		var file = Assert.Single(record.Files);
		Assert.Equal("app.cs", file.Path);
		Assert.Contains("-agent line", file.Delta, StringComparison.Ordinal);
		Assert.Contains("+user line", file.Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void NoUserAction_RecordsNothing() {
		Boundary("p1");
		AgentEdit("app.cs", "agent line\n");
		Boundary("p2");
		Boundary("p3");

		Assert.Equal(0, _corpus.Count);
	}

	[Fact]
	public void HandEditToAgentUntouchedRegion_IsNotACorrection() {
		// The user editing a region the agent didn't write is their own coding, not a correction — and autosave
		// fires on it repeatedly, so it must never register (the gate splices only agent-overlapping edits).
		_fs.WriteAllText(Abs("app.cs"), "one\ntwo\nthree\n");
		Boundary("p1");
		AgentEdit("app.cs", "ONE\ntwo\nthree\n"); // agent changed line 1 only
		HandEdit("app.cs", "ONE\ntwo\nTHREE\n"); // user changed line 3 — the agent never touched it

		Assert.Equal(0, _corpus.Count);
	}

	[Fact]
	public void HandEditToAgentHunk_ThenTypingOwnCode_RecordsOnlyTheAgentCorrection() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\n");
		Boundary("p1");
		AgentEdit("app.cs", "A\nb\n"); // agent changed line 1
		HandEdit("app.cs", "A2\nb\n"); // user corrects the agent's line 1 → recorded
		HandEdit("app.cs", "A2\nb\nCCC\n"); // user adds their own line 3 …
		HandEdit("app.cs", "A2\nb\nCCC2\n"); // … and keeps editing it (autosave)

		Assert.Equal(1, _corpus.Count);
	}

	[Fact]
	public void AppendingOwnLinesPastAgentContent_IsNotACorrection() {
		// The agent's edit reaches end-of-file (a created file); the user then types their OWN lines at the end.
		// That is new authoring adjacent to the agent's region, not a correction — and autosave fires on every
		// keystroke-pause, so it must never accumulate (regression: the trailing-boundary overlap bug).
		Boundary("p1");
		AgentEdit("app.cs", "agent1\nagent2\n");
		HandEdit("app.cs", "agent1\nagent2\nmine\n");
		HandEdit("app.cs", "agent1\nagent2\nmine\nmine2\n"); // keeps typing (autosave)

		Assert.Equal(0, _corpus.Count);
	}

	[Fact]
	public void InsertingBetweenAgentLines_IsACorrection() {
		Boundary("p1");
		AgentEdit("app.cs", "first\nsecond\n");
		HandEdit("app.cs", "first\nMIDDLE\nsecond\n"); // a line inserted between the agent's two lines

		var file = Assert.Single(Assert.Single(_corpus.ReadAll()).Files);
		Assert.Contains("+MIDDLE", file.Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void RepeatedSaveOfSameContent_RecordsOnce() {
		Boundary("p1");
		AgentEdit("app.cs", "agent\n");
		HandEdit("app.cs", "user\n");
		HandEdit("app.cs", "user\n"); // a second autosave of the same content

		Assert.Equal(1, _corpus.Count);
	}

	[Fact]
	public void RevertFile_RecordsReversal() {
		_fs.WriteAllText(Abs("app.cs"), "one\n");
		Boundary("p1");
		AgentEdit("app.cs", "one\ntwo\n");

		_tracker.RevertFile(Abs("app.cs"));

		var file = Assert.Single(Assert.Single(_corpus.ReadAll()).Files);
		Assert.Contains("-two", file.Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void RevertHunk_RecordsRejectedLines() {
		_fs.WriteAllText(Abs("app.cs"), "one\ntwo\n");
		Boundary("p1");
		AgentEdit("app.cs", "one\nADDED\ntwo\n");

		var outcome = _tracker.RevertHunk(Abs("app.cs"), new LineRange(2, 2), new LineRange(2, 3), "ADDED");

		Assert.Equal(RevertHunkOutcome.Reverted, outcome);
		var file = Assert.Single(Assert.Single(_corpus.ReadAll()).Files);
		Assert.Contains("-ADDED", file.Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void RevertManyTurnsAfterWrite_StillRecorded() {
		// The review model accumulates: a revert made long after the write still captures the reversal.
		_fs.WriteAllText(Abs("app.cs"), "one\n");
		Boundary("p1");
		AgentEdit("app.cs", "one\ntwo\n");
		Boundary("p2");
		Boundary("p3");
		Assert.Equal(0, _corpus.Count);

		_tracker.RevertFile(Abs("app.cs"));

		var record = Assert.Single(_corpus.ReadAll());
		Assert.Equal("p1", record.Prompt); // attributed to the turn that WROTE the line, not the latest boundary
		Assert.Contains("-two", Assert.Single(record.Files).Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void KeptFile_LaterHandEdits_AreNotCorrections() {
		// Keeping accepts the output and closes the correction window: the user's later edits are their own coding.
		Boundary("p1");
		AgentEdit("app.cs", "agent line\n");
		_tracker.KeepFile(Abs("app.cs"));

		HandEdit("app.cs", "user rewrite\n");

		Assert.Equal(0, _corpus.Count);
	}

	[Fact]
	public void HandEditBeforeKeepAll_IsRecordedAtTheSave() {
		Boundary("p1");
		AgentEdit("app.cs", "agent line\n");
		HandEdit("app.cs", "user line\n"); // recorded here, not deferred to a boundary

		_tracker.AcceptTurn();

		Assert.Equal(1, _corpus.Count);
	}

	[Fact]
	public void AgentDeletesItsOwnFile_NotACorrection() {
		Boundary("p1");
		AgentEdit("temp.cs", "scratch\n");
		_fs.DeleteFile(Abs("temp.cs"));
		// The delete reconciles at the agent's next completed tool — agent action, not a user revert.
		_tracker.Observe(new AgentToolCompleted(new AgentMutation.None()));

		Assert.Equal(0, _corpus.Count);
	}

	[Fact]
	public void UserRevertsCreatedFile_RecordsTheDeletion() {
		Boundary("p1");
		AgentEdit("new.cs", "created\n");

		_tracker.RevertFile(Abs("new.cs")); // created-since-baseline → the revert deletes it

		var file = Assert.Single(Assert.Single(_corpus.ReadAll()).Files);
		Assert.Equal("new.cs", file.Path);
		Assert.Contains("-created", file.Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void NullPromptTurn_RecordsWithNullPrompt() {
		Boundary(null); // Codex's turn/started carries no prompt
		AgentEdit("app.cs", "agent line\n");
		HandEdit("app.cs", "user line\n");

		Assert.Null(Assert.Single(_corpus.ReadAll()).Prompt);
	}

	private void Boundary(string? prompt) => _tracker.Observe(new AgentPromptSubmitted("session", prompt));

	private void AgentEdit(string relativePath, string content) {
		var mutation = new AgentMutation.File(relativePath, Cwd: null, ProvidesEditLocation: true);
		_tracker.Observe(new AgentToolStarting(mutation));
		_fs.WriteAllText(Abs(relativePath), content);
		_tracker.Observe(new AgentToolCompleted(mutation));
	}

	// A user editor save: the disk write the file provider does, then the capture the fs-write handler triggers.
	private void HandEdit(string relativePath, string content) {
		_fs.WriteAllText(Abs(relativePath), content);
		_tracker.RecordHandEdit(Abs(relativePath), content);
	}

	private string Abs(string relativePath) => Path.Combine(_root, relativePath);
}
