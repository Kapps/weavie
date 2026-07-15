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
	public void MixedAgentAndUserReplacement_RecordsOnlyTheAgentLine() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\n");
		Boundary("p1");
		AgentEdit("app.cs", "A\nb\n");

		HandEdit("app.cs", "A2\nB-OWN\n");

		var file = Assert.Single(Assert.Single(_corpus.ReadAll()).Files);
		Assert.Contains("+A2", file.Delta, StringComparison.Ordinal);
		Assert.DoesNotContain("B-OWN", file.Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void AdjacentOriginsChangedInOneSave_KeepTheirPrompts() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\n");
		Boundary("p1");
		AgentEdit("app.cs", "A\nb\n");
		Boundary("p2");
		AgentEdit("app.cs", "A\nB\n");

		HandEdit("app.cs", "A1\nB2\n");

		var records = _corpus.ReadAll();
		Assert.Equal(["p1", "p2"], records.Select(record => record.Prompt));
		Assert.Contains("+A1", Assert.Single(records[0].Files).Delta, StringComparison.Ordinal);
		Assert.Contains("+B2", Assert.Single(records[1].Files).Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void IgnoredUserEdit_RemainsUnattributedAfterLaterAgentEdit() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\nc\n");
		Boundary("p1");
		AgentEdit("app.cs", "A\nb\nc\n");
		HandEdit("app.cs", "A\nb\nC\n");
		Boundary("p2");
		AgentEdit("app.cs", "A\nB\nC\n");
		HandEdit("app.cs", "A\nB\nC2\n");

		Assert.Equal(0, _corpus.Count);

		HandEdit("app.cs", "A1\nB\nC2\n");
		HandEdit("app.cs", "A1\nB2\nC2\n");

		Assert.Equal(["p1", "p2"], _corpus.ReadAll().Select(record => record.Prompt));
	}

	[Fact]
	public void RevertAfterLaterAgentEdit_PreservesUnattributedUserText() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\nc\n");
		Boundary("p1");
		AgentEdit("app.cs", "A\nb\nc\n");
		HandEdit("app.cs", "A\nb\nC\n");
		Boundary("p2");
		AgentEdit("app.cs", "A\nB\nC\n");

		_tracker.RevertFile(Abs("app.cs"));

		Assert.Equal("a\nb\nC\n", _fs.ReadAllText(Abs("app.cs")));
	}

	[Fact]
	public void RevertAfterLaterAgentEdit_PreservesUnattributedInsertion() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\nc\n");
		Boundary("p1");
		AgentEdit("app.cs", "A\nb\nc\n");
		HandEdit("app.cs", "A\nb\nMINE\nc\n");
		Boundary("p2");
		AgentEdit("app.cs", "A\nB\nMINE\nc\n");

		_tracker.RevertFile(Abs("app.cs"));

		Assert.Equal("a\nb\nMINE\nc\n", _fs.ReadAllText(Abs("app.cs")));
	}

	[Fact]
	public void RestoringAgentDeletedLine_IsACorrection() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\nc\n");
		Boundary("remove b");
		AgentEdit("app.cs", "a\nc\n");

		HandEdit("app.cs", "a\nb\nc\n");

		var record = Assert.Single(_corpus.ReadAll());
		Assert.Equal("remove b", record.Prompt);
		Assert.Contains("+b", Assert.Single(record.Files).Delta, StringComparison.Ordinal);
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
	public void ProgressiveTypingOverOneAgentHunk_CoalescesToOneCorrection() {
		// The user types their replacement over an agent line; autosave fires on every keystroke-pause, so each
		// intermediate state reaches RecordHandEdit. Those are one correction, not five — they must coalesce into a
		// single agent → final-text entry rather than piling up the intermediate states.
		Boundary("p1");
		AgentEdit("app.cs", "agent line\n");
		HandEdit("app.cs", "u\n");
		HandEdit("app.cs", "us\n");
		HandEdit("app.cs", "use\n");
		HandEdit("app.cs", "user\n");

		var record = Assert.Single(_corpus.ReadAll());
		Assert.Equal("p1", record.Prompt);
		var file = Assert.Single(record.Files);
		Assert.Contains("-agent line", file.Delta, StringComparison.Ordinal); // anchored at the original agent text …
		Assert.Contains("+user", file.Delta, StringComparison.Ordinal); // … through to the final content
		Assert.DoesNotContain("+us\n", file.Delta, StringComparison.Ordinal); // no intermediate keystroke states
	}

	[Fact]
	public void AlternatingCorrectionsToTwoRegionsOfOneAgentEdit_CoalesceIndependently() {
		// One agent edit mints one origin across BOTH hunks it wrote (lines 1 and 3). The user then alternates
		// corrections between those two regions across autosaves. Sharing an origin must not collapse them into one
		// chain: each region coalesces to its own agent-output → final entry, none left at an intermediate state.
		_fs.WriteAllText(Abs("app.cs"), "x1\nx2\nx3\n");
		Boundary("p1");
		AgentEdit("app.cs", "A\nx2\nC\n"); // one edit, one origin, two non-contiguous hunks

		HandEdit("app.cs", "A1\nx2\nC\n"); // region 1 …
		HandEdit("app.cs", "A12\nx2\nC\n"); // … keeps typing (coalesces)
		HandEdit("app.cs", "A12\nx2\nC1\n"); // now region 2 …
		HandEdit("app.cs", "A12\nx2\nC12\n"); // … keeps typing (coalesces)

		var records = _corpus.ReadAll();
		Assert.Equal(["p1", "p1"], records.Select(record => record.Prompt));
		Assert.Contains("+A12", Assert.Single(records[0].Files).Delta, StringComparison.Ordinal);
		Assert.DoesNotContain("+A1\n", records[0].Files[0].Delta, StringComparison.Ordinal); // not an intermediate
		Assert.Contains("+C12", Assert.Single(records[1].Files).Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void RetypingARegionBackToAgentOutput_DropsTheCorrection() {
		// The user edits the agent's line, then keeps typing until it matches the agent's output again — the
		// correction vanished, so its corpus entry must be dropped, not left at the last intermediate state.
		Boundary("p1");
		AgentEdit("app.cs", "agent\n");
		HandEdit("app.cs", "agnt\n"); // a correction is recorded …
		Assert.Equal(1, _corpus.Count);

		HandEdit("app.cs", "agent\n"); // … then retyped back to the agent's output

		Assert.Equal(0, _corpus.Count);
	}

	[Fact]
	public void CorrectionsToTwoRegionsAcrossSaves_CoalesceIndependently() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\n");
		Boundary("p1");
		AgentEdit("app.cs", "A\nb\n"); // agent wrote line 1
		Boundary("p2");
		AgentEdit("app.cs", "A\nB\n"); // agent wrote line 2

		HandEdit("app.cs", "A1\nB\n"); // correct line 1 …
		HandEdit("app.cs", "A12\nB\n"); // … keep typing on it (coalesces)
		HandEdit("app.cs", "A12\nB1\n"); // now correct line 2 …
		HandEdit("app.cs", "A12\nB12\n"); // … keep typing on it (coalesces)

		var records = _corpus.ReadAll();
		Assert.Equal(["p1", "p2"], records.Select(record => record.Prompt));
		Assert.Contains("+A12", Assert.Single(records[0].Files).Delta, StringComparison.Ordinal);
		Assert.Contains("+B12", Assert.Single(records[1].Files).Delta, StringComparison.Ordinal);
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
	public void RevertHunk_AfterIgnoredInsertion_MapsTheDiskGuard() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\nc\nd\n");
		Boundary("p1");
		AgentEdit("app.cs", "a\nb\nc\nD\n");
		HandEdit("app.cs", "a\nMINE\nb\nc\nD\n");

		var outcome = _tracker.RevertHunk(Abs("app.cs"), new LineRange(4, 5), new LineRange(4, 5), "D");

		Assert.Equal(RevertHunkOutcome.Reverted, outcome);
		Assert.Equal("a\nMINE\nb\nc\nd\n", _fs.ReadAllText(Abs("app.cs")));
	}

	[Fact]
	public void KeepHunk_AfterIgnoredInsertion_MapsTheDiskGuard() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\nc\nd\n");
		Boundary("p1");
		AgentEdit("app.cs", "a\nb\nc\nD\n");
		HandEdit("app.cs", "a\nMINE\nb\nc\nD\n");

		Assert.True(_tracker.KeepHunk(Abs("app.cs"), new LineRange(4, 5), new LineRange(4, 5), "D"));
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
	public void RevertAll_GroupsFilesByProducingPrompt() {
		Boundary("p1");
		AgentEdit("one.cs", "one\n");
		Boundary("p2");
		AgentEdit("two.cs", "two\n");

		_tracker.RevertAll();

		Assert.Equal(["p1", "p2"], _corpus.ReadAll().Select(record => record.Prompt));
	}

	[Fact]
	public void RevertFile_SlicesAdjacentChangesByProducingPrompt() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\n");
		Boundary("p1");
		AgentEdit("app.cs", "A\nb\n");
		Boundary("p2");
		AgentEdit("app.cs", "A\nB\n");

		_tracker.RevertFile(Abs("app.cs"));

		var records = _corpus.ReadAll();
		Assert.Equal(["p1", "p2"], records.Select(record => record.Prompt));
		Assert.Contains("-A", Assert.Single(records[0].Files).Delta, StringComparison.Ordinal);
		Assert.DoesNotContain("-B", records[0].Files[0].Delta, StringComparison.Ordinal);
		Assert.Contains("-B", Assert.Single(records[1].Files).Delta, StringComparison.Ordinal);
		Assert.DoesNotContain("-A", records[1].Files[0].Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void RevertFile_SlicesSequentialDeletionsAtOneGapByProducingPrompt() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\nc\n");
		Boundary("p1");
		AgentEdit("app.cs", "a\nc\n");
		Boundary("p2");
		AgentEdit("app.cs", "a\n");

		_tracker.RevertFile(Abs("app.cs"));

		var records = _corpus.ReadAll();
		Assert.Equal(["p1", "p2"], records.Select(record => record.Prompt));
		Assert.Contains("+b", Assert.Single(records[0].Files).Delta, StringComparison.Ordinal);
		Assert.DoesNotContain("+c", records[0].Files[0].Delta, StringComparison.Ordinal);
		Assert.Contains("+c", Assert.Single(records[1].Files).Delta, StringComparison.Ordinal);
		Assert.DoesNotContain("+b", records[1].Files[0].Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void RestoringAdjacentDeletedOrigins_KeepsTheirPrompts() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\n");
		Boundary("p1");
		AgentEdit("app.cs", "A\nb\n");
		Boundary("p2");
		AgentEdit("app.cs", "A\nB\n");
		HandEdit("app.cs", string.Empty);

		HandEdit("app.cs", "A\nB\n");

		Assert.Equal(["p1", "p2", "p1", "p2"], _corpus.ReadAll().Select(record => record.Prompt));
	}

	[Fact]
	public void PartialDeletionRestore_PreservesTheRemainingProvenance() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\nc\n");
		Boundary("p1");
		AgentEdit("app.cs", "a\n");

		HandEdit("app.cs", "a\nb\n");
		HandEdit("app.cs", "a\nb\nc\n");

		var records = _corpus.ReadAll();
		Assert.Equal(2, records.Count);
		Assert.Contains("+b", Assert.Single(records[0].Files).Delta, StringComparison.Ordinal);
		Assert.Contains("+c", Assert.Single(records[1].Files).Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void ReverseSequentialDeletionRestore_MatchesPromptsByDeletedText() {
		_fs.WriteAllText(Abs("app.cs"), "a\nb\nc\n");
		Boundary("p1");
		AgentEdit("app.cs", "a\nb\n");
		Boundary("p2");
		AgentEdit("app.cs", "a\n");

		HandEdit("app.cs", "a\nb\nc\n");

		var records = _corpus.ReadAll();
		Assert.Equal(["p1", "p2"], records.Select(record => record.Prompt));
		Assert.Contains("+c", Assert.Single(records[0].Files).Delta, StringComparison.Ordinal);
		Assert.Contains("+b", Assert.Single(records[1].Files).Delta, StringComparison.Ordinal);
	}

	[Fact]
	public void UndoKeep_RestoresCorrectionProvenance() {
		Boundary("p1");
		AgentEdit("app.cs", "agent\n");
		_tracker.KeepFile(Abs("app.cs"));
		Assert.True(_tracker.UndoLastKeep().Acted);

		HandEdit("app.cs", "user\n");

		Assert.Equal("p1", Assert.Single(_corpus.ReadAll()).Prompt);
	}

	[Fact]
	public void UndoRevert_RestoresCorrectionProvenance() {
		_fs.WriteAllText(Abs("app.cs"), "base\n");
		Boundary("p1");
		AgentEdit("app.cs", "agent\n");
		_tracker.RevertFile(Abs("app.cs"));
		Assert.True(_tracker.UndoLastRevert().Acted);

		HandEdit("app.cs", "user\n");

		Assert.Equal(2, _corpus.Count);
		Assert.All(_corpus.ReadAll(), record => Assert.Equal("p1", record.Prompt));
	}

	[Fact]
	public void RedoRevert_RemovesCorrectionProvenanceAgain() {
		_fs.WriteAllText(Abs("app.cs"), "base\n");
		Boundary("p1");
		AgentEdit("app.cs", "agent\n");
		_tracker.RevertFile(Abs("app.cs"));
		_tracker.UndoLastRevert();
		Assert.True(_tracker.Redo().Acted);

		HandEdit("app.cs", "user\n");

		Assert.Equal(1, _corpus.Count);
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

	[Fact]
	public void InFlightRegionsBeyondCap_EvictsOldestRegion_StoppingItsCoalescing() {
		// Open MaxInFlightRegions distinct regions, each left correcting-but-unfinished (never retyped back to
		// the agent's output), then open one more. The dictionary must not just grow forever: the oldest region
		// (region 0) should have been evicted, so its next save starts a FRESH chain (a new corpus append)
		// instead of coalescing into (replacing) its existing entry.
		for (int i = 0; i < CorrectionRecorder.MaxInFlightRegions; i++) {
			Boundary($"p{i}");
			AgentEdit($"file{i}.cs", "agent\n");
			HandEdit($"file{i}.cs", "correcting\n");
		}

		Boundary("overflow");
		AgentEdit("overflow.cs", "agent\n");
		HandEdit("overflow.cs", "correcting\n");

		int countBeforeContinuedEdit = _corpus.Count;
		HandEdit("file0.cs", "correcting-more\n"); // region 0's chain, if still tracked, would coalesce in place

		Assert.Equal(countBeforeContinuedEdit + 1, _corpus.Count); // a fresh append, not a same-count coalesce
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
