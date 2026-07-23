using Weavie.Core.Agents;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Changes;

/// <summary>
/// Records every file changed during the session and the diff against each file's session baseline. Provider
/// events feed <see cref="Observe"/> from hook or native structured-agent streams; the lock protects their threads.
/// <para>
/// Scoped by <c>isInScope</c> to the session's worktree (+ scratch): an out-of-scope edit is dropped. The
/// tracker's scope must match the editor's <c>file://</c> provider scope, else an open is pushed for a path
/// the editor can't read, stranding a blank tab.
/// </para>
/// </summary>
public sealed partial class SessionChangeTracker {
	private readonly IFileSystem _fileSystem;
	private readonly string _workspaceRoot;
	private readonly Func<string, bool> _isInScope;
	private readonly object _gate = new();
	private readonly Dictionary<string, string> _baseline = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _current = new(StringComparer.Ordinal);
	// Each file's last-reviewed content; advanced only on keep-all (AcceptTurn) or a per-hunk revert, not on a
	// turn boundary, so the review set accumulates everything unacknowledged across turns (docs/specs/turn-review.md).
	private readonly Dictionary<string, string> _reviewBaseline = new(StringComparer.Ordinal);
	// Each file's content at the last commit point — keep-all (AcceptTurn) or a turn boundary (CommitAccepted).
	// The faded "accepted" band is acceptedAnchor→reviewBaseline (kept-but-uncommitted): a kept hunk stays
	// visible-but-faded with an inline undo until a commit clears it. See docs/specs/turn-review.md (Phase 2).
	private readonly Dictionary<string, string> _acceptedAnchor = new(StringComparer.Ordinal);
	// Each file's content at the most recent edit's PreToolUse; diffed against post-edit in EditLocationFor.
	private readonly Dictionary<string, string> _preEdit = new(StringComparer.Ordinal);
	// Non-text files never enter the diff dictionaries; their stat is enough to refresh an open media/editor
	// surface when a workspace-wide tool changes them without serializing their contents.
	private readonly Dictionary<string, FileStat> _nonText = new(StringComparer.Ordinal);
	// Files absent on disk when their review baseline was captured, so reverting their last hunk deletes rather
	// than leaves a 0-byte file. Keys off existence-at-baseline, not emptiness.
	private readonly HashSet<string> _createdSinceBaseline = new(StringComparer.Ordinal);

	/// <summary>Creates a tracker that reads file content through <paramref name="fileSystem"/>.</summary>
	/// <param name="fileSystem">The session filesystem the tracker reads changed-file content through.</param>
	/// <param name="workspaceRoot">
	/// The session's worktree root — the root <c>reveal-file</c> resolves relative paths against, so every
	/// <see cref="EditLocationFor"/> jump link is relative to it (never to Claude's drifting cwd).
	/// </param>
	/// <param name="isInScope">
	/// Predicate over an absolute path: only edits it accepts are tracked. See the type remarks.
	/// </param>
	public SessionChangeTracker(IFileSystem fileSystem, string workspaceRoot, Func<string, bool> isInScope) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentNullException.ThrowIfNull(isInScope);
		_fileSystem = fileSystem;
		_workspaceRoot = workspaceRoot;
		_isInScope = isInScope;
	}

	/// <summary>Raised whenever the change set updates (a file's current content was recorded).</summary>
	public event Action? Changed;

	/// <summary>
	/// Raised with the absolute path of a file whose content was just recorded, so the host can push a
	/// targeted live-refresh of that one file's editor model. Fires alongside <see cref="Changed"/>.
	/// </summary>
	public event Action<string>? FileChanged;

	/// <summary>
	/// Raised with the absolute path of a tracked file that disappeared from disk (a <c>Bash</c> rm/rename/temp
	/// cleanup), so the host can close its editor tab. Fires from <see cref="Observe"/>'s post-tool reconciliation.
	/// </summary>
	public event Action<string>? FileDeleted;

	/// <summary>
	/// Raised with the paths whose faded accepted band was just committed at a turn boundary (see
	/// <see cref="Observe"/>), so the host can re-push the trimmed review set, each path's diff, and the
	/// (now cleared) undo history.
	/// </summary>
	public event Action<IReadOnlyList<string>>? AcceptedCommitted;

	/// <summary>
	/// Folds provider events into the change set. Direct mutations capture and record their paths; prompt boundaries
	/// commit accepted hunks. Unenumerated shell and tool side-effects are not discovered by scanning the workspace.
	/// </summary>
	public void Observe(AgentEvent value) {
		ArgumentNullException.ThrowIfNull(value);

		if (value is AgentPromptSubmitted submitted) {
			lock (_gate) {
				_currentPrompt = submitted.Prompt;
			}

			CommitAccepted();
			return;
		}

		if (value is AgentTurnStopped) {
			return;
		}

		// Reconcile deletions before recording this tool's edit, so a Bash rm/mv drops the vanished file first.
		if (value is AgentToolCompleted) {
			ReconcileDeletions();
		}

		var mutation = value switch {
			AgentToolStarting starting => starting.Mutation,
			AgentToolCompleted completed => completed.Mutation,
			_ => new AgentMutation.None(),
		};
		var paths = ExtractEditPaths(mutation).Where(_isInScope).ToList();
		if (paths.Count == 0) {
			return;
		}

		if (value is AgentToolStarting) {
			foreach (string path in paths) {
				CaptureBaseline(path);
			}
		} else if (value is AgentToolCompleted) {
			foreach (string path in paths) {
				RecordChange(path);
			}
		}
	}

	/// <summary>
	/// Keep-all: advances every review baseline AND accepted anchor to current content, clearing every inline
	/// marker (bright pending and faded accepted alike). The session diff (vs the session baseline) is kept, and
	/// nothing counts as "created since baseline" any more.
	/// </summary>
	public void AcceptTurn() {
		lock (_gate) {
			_reviewBaseline.Clear();
			_acceptedAnchor.Clear();
			foreach (var (path, content) in _current) {
				_reviewBaseline[path] = content;
				_acceptedAnchor[path] = content; // commit point: the faded band collapses to nothing
			}

			_createdSinceBaseline.Clear();
			_provenance.Clear();
			// Keep-all is the commit point — accepted changes are locked in, so the undo history resets here.
			_undoStack.Clear();
			_redoStack.Clear();
		}
	}

	// Commit point shared by the turn boundary and a settled review: each accepted anchor advances to its review
	// baseline (faded band collapses, pending stays). The history clears too — undoing a stale keep/revert would
	// restore an old anchor, resurrecting committed hunks.
	private void CommitAccepted() {
		List<string>? committed = null;
		lock (_gate) {
			foreach (var (path, baseline) in _reviewBaseline) {
				if (!string.Equals(_acceptedAnchor.GetValueOrDefault(path, baseline), baseline, StringComparison.Ordinal)) {
					_acceptedAnchor[path] = baseline;
					(committed ??= []).Add(path);
				}
			}

			if (committed is null) {
				return;
			}

			PurgeAcceptedProvenance();
			_undoStack.Clear();
			_redoStack.Clear();
		}

		AcceptedCommitted?.Invoke(committed);
	}

	// Exits the review once its last pending hunk resolves: when every file in the set is fully kept or reverted
	// (faded bands only, no bright hunk anywhere), commit the accepted bands so the set empties — resolving the
	// final change ends the review without a separate keep-all. Called by each keep/revert after it acts.
	private void CommitAcceptedIfSettled() {
		lock (_gate) {
			bool fadedOnly = false;
			foreach (var (path, accepted) in _acceptedAnchor) {
				if (_current.TryGetValue(path, out string? current) && !string.Equals(accepted, current, StringComparison.Ordinal)) {
					if (!string.Equals(_reviewBaseline.GetValueOrDefault(path, accepted), current, StringComparison.Ordinal)) {
						return; // a bright pending hunk remains — the review is still in progress
					}

					fadedOnly = true;
				}
			}

			if (!fadedOnly) {
				return; // no review set at all — nothing to exit
			}
		}

		CommitAccepted();
	}

	/// <summary>Snapshots <paramref name="path"/>'s current content as its session + review baseline, once.</summary>
	/// <param name="path">Absolute file path.</param>
	public void CaptureBaseline(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			// Disk content here is the pre-edit file. Seed baselines if missing; track existence so a later revert
			// deletes rather than truncates a file that didn't yet exist.
			bool existed = _fileSystem.FileExists(path);
			if (!TryReadOrEmpty(path, out string content)) {
				Forget(path);
				if (existed && _fileSystem.TryGetStat(path, out var stat)) {
					_nonText.TryAdd(path, stat);
				}
				return;
			}

			_nonText.Remove(path);
			CaptureBaselineLocked(path, content, existed);
		}
	}

	private void CaptureBaselineLocked(string path, string content, bool existed) {
		CaptureProvenanceBaseline(path, content);
		_baseline.TryAdd(path, content);
		if (_reviewBaseline.TryAdd(path, content) && !existed) {
			_createdSinceBaseline.Add(path);
		}

		_acceptedAnchor.TryAdd(path, content); // seeded == reviewBaseline; diverges only as hunks are kept
		_preEdit[path] = content;
	}

	/// <summary>Records <paramref name="path"/>'s latest content (baselining to empty if it appeared this session).</summary>
	/// <param name="path">Absolute file path.</param>
	public void RecordChange(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		bool reviewRemoved;
		bool nonTextChanged;
		bool ignoredNonText;
		lock (_gate) {
			if (!TryReadOrEmpty(path, out string content)) {
				ignoredNonText = true;
				reviewRemoved = _current.ContainsKey(path);
				nonTextChanged = _fileSystem.TryGetStat(path, out var stat)
					&& (!_nonText.TryGetValue(path, out var known) || stat.MtimeMs != known.MtimeMs || stat.Size != known.Size);
				if (stat.Exists) {
					_nonText[path] = stat;
				}
				Forget(path);
			} else {
				ignoredNonText = false;
				if (_nonText.Remove(path)) {
					// A binary baseline cannot support a safe line-level reject; refresh the now-text file without review.
					reviewRemoved = false;
					nonTextChanged = true;
				} else {
					reviewRemoved = false;
					nonTextChanged = false;
					_baseline.TryAdd(path, string.Empty);
					// First review-touch with no prior CaptureBaseline: the file appeared this session, so it didn't exist at baseline.
					if (_reviewBaseline.TryAdd(path, string.Empty)) {
						_createdSinceBaseline.Add(path);
					}

					_acceptedAnchor.TryAdd(path, string.Empty);
					string before = _preEdit.GetValueOrDefault(path, _current.GetValueOrDefault(path, string.Empty));
					string reviewCurrent = _current.GetValueOrDefault(path, before);
					_current[path] = RecordAgentProvenance(path, before, content, reviewCurrent);
				}
			}
		}

		if (reviewRemoved) {
			Changed?.Invoke();
		}

		if (ignoredNonText) {
			if (nonTextChanged) {
				FileChanged?.Invoke(path);
			}
			return;
		}

		if (nonTextChanged) {
			FileChanged?.Invoke(path);
			return;
		}

		if (reviewRemoved) {
			return;
		}

		Changed?.Invoke();
		FileChanged?.Invoke(path);
	}

	/// <summary>
	/// Seeds <paramref name="path"/>'s review state from a git ref instead of disk-at-first-edit, so a ref diff (a
	/// PR's base→head, or "diff against &lt;ref&gt;") reviews through the same engine as a turn: session + review
	/// baseline + accepted anchor all = <paramref name="refContent"/>, current + pre-edit = <paramref name="diskContent"/>.
	/// Records no undo history (a seed isn't a user action) and does NOT raise <see cref="Changed"/> — the host
	/// pushes the armed set.
	/// <para>
	/// Overwrites (not <c>TryAdd</c>) every baseline: if a live Claude edit already seeded a disk baseline for this
	/// file, that baseline is corrected back to the ref while its current content is kept, so the committed diff and
	/// the new edit accumulate into one bright band. Composes with <see cref="CaptureBaseline"/>'s <c>TryAdd</c>,
	/// which then no-ops on the already-seeded keys.
	/// </para>
	/// </summary>
	/// <param name="path">Absolute file path (inside the worktree, so it satisfies the tracker's scope).</param>
	/// <param name="refContent">The file's content at the diff ref — the baseline the current file is diffed against.</param>
	/// <param name="diskContent">The file's current worktree content.</param>
	/// <param name="existedAtRef">
	/// Whether the file existed at the ref; <see langword="false"/> marks it created-since-baseline, so reverting its
	/// last hunk deletes it rather than leaving a 0-byte file.
	/// </param>
	public void SeedRefBaseline(string path, string refContent, string diskContent, bool existedAtRef) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(refContent);
		ArgumentNullException.ThrowIfNull(diskContent);
		lock (_gate) {
			_baseline[path] = refContent;
			_reviewBaseline[path] = refContent;
			_acceptedAnchor[path] = refContent;
			_current[path] = diskContent;
			_preEdit[path] = diskContent;
			SeedProvenance(path, diskContent);
			if (existedAtRef) {
				_createdSinceBaseline.Remove(path);
			} else {
				_createdSinceBaseline.Add(path);
			}
		}
	}

	/// <summary>
	/// Drops any recorded file that no longer exists on disk (deleted out from under us), since there's nothing to
	/// render. Raises <see cref="FileDeleted"/> per removed path and a single <see cref="Changed"/>; a no-op when
	/// nothing vanished.
	/// </summary>
	private void ReconcileDeletions() {
		List<string>? removed = null;
		lock (_gate) {
			// Snapshot keys first: Forget mutates _current while we iterate.
			foreach (string path in new List<string>(_current.Keys)) {
				if (!_fileSystem.FileExists(path)) {
					Forget(path);
					(removed ??= []).Add(path);
				}
			}

			foreach (string path in new List<string>(_nonText.Keys)) {
				if (!_fileSystem.FileExists(path)) {
					_nonText.Remove(path);
					(removed ??= []).Add(path);
				}
			}
		}

		if (removed is null) {
			return;
		}

		foreach (string path in removed) {
			FileDeleted?.Invoke(path);
		}

		Changed?.Invoke();
	}

	/// <summary>
	/// Reverts a single hunk on disk, sourcing replacement text from Core's review baseline (never from message
	/// content — the hook-bridge security rule). <paramref name="guardText"/> is an optimistic-concurrency check:
	/// a mismatch against the file's current lines aborts the write (<see cref="RevertHunkOutcome.GuardMismatch"/>).
	/// A created-since-baseline file the revert empties is deleted (<see cref="RevertHunkOutcome.Deleted"/>), not truncated.
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	/// <param name="baselineRange">The hunk's range in the review baseline (1-based, end-exclusive).</param>
	/// <param name="currentRange">The hunk's range in the current file (1-based, end-exclusive).</param>
	/// <param name="guardText">The exact current text of <paramref name="currentRange"/> as the web sees it.</param>
	public RevertHunkOutcome RevertHunk(string path, LineRange baselineRange, LineRange currentRange, string guardText) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(guardText);
		List<CorrectionEdit> edits;
		RevertHunkOutcome outcome;
		lock (_gate) {
			string currentRaw = _current.GetValueOrDefault(path, ReadOrEmpty(path));
			var currentLines = SplitLines(currentRaw);
			var diskLines = SplitLines(ReadOrEmpty(path));
			// currentRange + guardText are in the live-model (== disk) space the web diffed, so guard against disk
			// directly; then map the range into _current space (which omits the user's non-agent edits) so the
			// review-side revert below rewrites only the agent's lines and preserves the user's untouched ones.
			if (!TryGetSlice(diskLines, currentRange, out var guardedSlice)
				|| !string.Equals(string.Join("\n", guardedSlice), guardText, StringComparison.Ordinal)) {
				return RevertHunkOutcome.GuardMismatch;
			}

			var currentInCurrent = MapActualRangeToCurrent(path, currentRange);
			if (!TryGetSlice(currentLines, currentInCurrent, out _)) {
				return RevertHunkOutcome.GuardMismatch;
			}

			var baselineLines = SplitLines(_reviewBaseline.GetValueOrDefault(path, string.Empty));
			if (!TryGetSlice(baselineLines, baselineRange, out var replacement)) {
				return RevertHunkOutcome.GuardMismatch;
			}

			var newLines = new List<string>(currentLines);
			newLines.RemoveRange(currentInCurrent.Start - 1, currentInCurrent.EndExclusive - currentInCurrent.Start);
			newLines.InsertRange(currentInCurrent.Start - 1, replacement);
			string newContent = JoinLines(newLines, currentRaw);

			// The rejected hunk is the correction: the agent's lines out, the baseline's back in.
			edits = CorrectionsForRevert(path, currentInCurrent, baselineRange);
			var before = Capture(path, withDisk: true);
			// Reverting the last hunk of a created file returns it to non-existence — delete and forget it.
			string diskContent = ApplyReviewChange(path, currentRaw, newContent);
			if (diskContent.Length == 0 && _createdSinceBaseline.Contains(path)) {
				_fileSystem.DeleteFile(path);
				Forget(path);
				outcome = RevertHunkOutcome.Deleted;
			} else {
				_fileSystem.WriteAllText(path, diskContent);
				_current[path] = newContent;
				outcome = RevertHunkOutcome.Reverted;
			}

			Record(ReviewActionKind.Revert, touchesDisk: true, currentRange.Start, [before], [path]);
		}

		RaiseCorrected(edits);
		if (outcome != RevertHunkOutcome.GuardMismatch) {
			CommitAcceptedIfSettled();
		}

		return outcome;
	}

	/// <summary>
	/// Reverts a whole file to its review baseline (per-file undo). A created-since-baseline file is deleted, not
	/// truncated. No guard — the whole file is reset, not a single hunk against concurrent edits.
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	public RevertHunkOutcome RevertFile(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		List<CorrectionEdit> edits;
		RevertHunkOutcome outcome;
		lock (_gate) {
			if (!_reviewBaseline.ContainsKey(path)) {
				return RevertHunkOutcome.GuardMismatch;
			}

			edits = RevertCorrections(path);
			var before = Capture(path, withDisk: true);
			outcome = RevertFileLocked(path);
			Record(ReviewActionKind.Revert, touchesDisk: true, line: null, [before], [path]);
		}

		RaiseCorrected(edits);
		if (outcome != RevertHunkOutcome.GuardMismatch) {
			CommitAcceptedIfSettled();
		}

		return outcome;
	}

	/// <summary>
	/// Reverts every file in the review set to its baseline on disk as a single undoable step (the whole-set
	/// analogue of <see cref="RevertFile"/>). A no-op (and not recorded) when the set is empty.
	/// </summary>
	public ReviewHistoryResult RevertAll() {
		List<CorrectionEdit> edits;
		ReviewHistoryResult result;
		lock (_gate) {
			var paths = new List<string>();
			foreach (var (path, baseline) in _reviewBaseline) {
				if (_current.TryGetValue(path, out string? current) && !string.Equals(baseline, current, StringComparison.Ordinal)) {
					paths.Add(path);
				}
			}

			if (paths.Count == 0) {
				return ReviewHistoryResult.Blocked(false);
			}

			edits = [];
			foreach (string path in paths) {
				edits.AddRange(RevertCorrections(path));
			}

			var before = paths.ConvertAll(p => Capture(p, withDisk: true));
			foreach (string path in paths) {
				RevertFileLocked(path);
			}

			Record(ReviewActionKind.Revert, touchesDisk: true, line: null, before, paths);
			result = ReviewHistoryResult.Done(true, paths, null);
		}

		RaiseCorrected(edits);
		// Deliberately no settled-commit here: revert-all is the bulk mistake-recovery gesture and must stay one
		// undoable step — a commit would lock kept hunks in and wipe the undo that recovers them.
		return result;
	}

	// A whole-file revert's correction: the agent's current content out, the review baseline back in (empty when
	// reverting a created file deletes it). Read before RevertFileLocked, which may forget the path. Holds _gate.
	private List<CorrectionEdit> RevertCorrections(string path) {
		string current = _current.GetValueOrDefault(path, string.Empty);
		string baseline = _reviewBaseline.GetValueOrDefault(path, string.Empty);
		return CorrectionsForRevert(
			path,
			new LineRange(1, LineDiff.SplitLines(current).Length + 1),
			new LineRange(1, LineDiff.SplitLines(baseline).Length + 1));
	}

	// The revert-file body without locking or history, so RevertFile and RevertAll share it. Holds _gate.
	private RevertHunkOutcome RevertFileLocked(string path) {
		string baseline = _reviewBaseline.GetValueOrDefault(path, string.Empty);
		string current = _current.GetValueOrDefault(path, string.Empty);
		string diskContent = ApplyReviewChange(path, current, baseline);
		if (diskContent.Length == 0 && _createdSinceBaseline.Contains(path)) {
			_fileSystem.DeleteFile(path);
			Forget(path);
			return RevertHunkOutcome.Deleted;
		}

		_fileSystem.WriteAllText(path, diskContent);
		_current[path] = baseline;
		return RevertHunkOutcome.Reverted;
	}

	/// <summary>
	/// Keeps a single hunk: advances the file's review baseline over just that hunk so it leaves the pending diff
	/// for good (and survives session switches), without touching disk. <paramref name="guardText"/> is the same
	/// optimistic-concurrency check as <see cref="RevertHunk"/>; a mismatch aborts and returns <see langword="false"/>.
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	/// <param name="baselineRange">The hunk's range in the review baseline (1-based, end-exclusive).</param>
	/// <param name="currentRange">The hunk's range in the current file (1-based, end-exclusive).</param>
	/// <param name="guardText">The exact current text of <paramref name="currentRange"/> as the web sees it.</param>
	public bool KeepHunk(string path, LineRange baselineRange, LineRange currentRange, string guardText) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(guardText);
		lock (_gate) {
			// currentRange + guardText are in the live-model (== disk) space the web diffed, so guard and take the
			// kept lines straight from disk — never remap through _current, which omits the user's non-agent edits.
			string diskRaw = ReadOrEmpty(path);
			var diskLines = SplitLines(diskRaw);
			if (!TryGetSlice(diskLines, currentRange, out var currentSlice)
				|| !string.Equals(string.Join("\n", currentSlice), guardText, StringComparison.Ordinal)) {
				return false;
			}

			string baselineRaw = _reviewBaseline.GetValueOrDefault(path, string.Empty);
			var baselineLines = SplitLines(baselineRaw);
			if (!TryGetSlice(baselineLines, baselineRange, out _)) {
				return false;
			}

			var before = Capture(path, withDisk: false);
			baselineLines.RemoveRange(baselineRange.Start - 1, baselineRange.EndExclusive - baselineRange.Start);
			baselineLines.InsertRange(baselineRange.Start - 1, currentSlice);
			_reviewBaseline[path] = JoinLines(baselineLines, baselineRaw.Length > 0 ? baselineRaw : diskRaw);
			SetPending(path, currentRange, false);
			Record(ReviewActionKind.Keep, touchesDisk: false, currentRange.Start, [before], [path]);
		}

		CommitAcceptedIfSettled();
		return true;
	}

	/// <summary>
	/// Keeps a whole file: advances its review baseline to current content so the file leaves the pending diff for
	/// good (and survives session switches), without touching disk. No-op for an untracked path.
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	public void KeepFile(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			if (!_current.ContainsKey(path)) {
				return;
			}

			var before = Capture(path, withDisk: false);
			string current = _current[path];
			_reviewBaseline[path] = current;
			SetAllPending(path, false);
			// No-op keep (already at baseline) records nothing, so its undo wouldn't surprise with an empty step.
			if (!string.Equals(before.ReviewBaseline, current, StringComparison.Ordinal)) {
				Record(ReviewActionKind.Keep, touchesDisk: false, line: null, [before], [path]);
			}
		}

		CommitAcceptedIfSettled();
	}

	/// <summary>
	/// Un-keeps a single accepted (faded) hunk: splices the accepted anchor's lines back into the review baseline
	/// over that hunk, so it returns to the bright pending band. The inverse of <see cref="KeepHunk"/> — but it
	/// operates on the accepted-anchor→review-baseline span (both Core-internal) and touches neither disk nor the
	/// undo history, so it composes safely with the LIFO keep/revert stack (a stale stack entry just declines via
	/// its own guard). Both sides are guarded: <paramref name="guardText"/> is the review-baseline text the web
	/// diffed, <paramref name="acceptedGuardText"/> the accepted-anchor text it would splice back — a mismatch on
	/// either (a concurrent keep moved the baseline, or a turn boundary committed the anchor) aborts with
	/// <see langword="false"/>, so the splice can only ever restore exactly the lines the user saw.
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	/// <param name="acceptedRange">The hunk's range in the accepted anchor — the source of the restored lines (1-based, end-exclusive).</param>
	/// <param name="reviewRange">The hunk's range in the review baseline — where the accepted lines are spliced back (1-based, end-exclusive).</param>
	/// <param name="acceptedGuardText">The exact accepted-anchor text of <paramref name="acceptedRange"/> as the web sees it.</param>
	/// <param name="guardText">The exact review-baseline text of <paramref name="reviewRange"/> as the web sees it.</param>
	public bool UnkeepHunk(string path, LineRange acceptedRange, LineRange reviewRange, string acceptedGuardText, string guardText) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(acceptedGuardText);
		ArgumentNullException.ThrowIfNull(guardText);
		lock (_gate) {
			string reviewRaw = _reviewBaseline.GetValueOrDefault(path, string.Empty);
			var reviewLines = SplitLines(reviewRaw);
			if (!TryGetSlice(reviewLines, reviewRange, out var reviewSlice)
				|| !string.Equals(string.Join("\n", reviewSlice), guardText, StringComparison.Ordinal)) {
				return false;
			}

			var acceptedLines = SplitLines(_acceptedAnchor.GetValueOrDefault(path, string.Empty));
			if (!TryGetSlice(acceptedLines, acceptedRange, out var replacement)
				|| !string.Equals(string.Join("\n", replacement), acceptedGuardText, StringComparison.Ordinal)) {
				return false;
			}

			reviewLines.RemoveRange(reviewRange.Start - 1, reviewRange.EndExclusive - reviewRange.Start);
			reviewLines.InsertRange(reviewRange.Start - 1, replacement);
			_reviewBaseline[path] = JoinLines(reviewLines, reviewRaw); // disk + _current untouched — the hunk just goes bright again
			foreach (var hunk in LineHunker.Hunks(
				LineDiff.SplitLines(_reviewBaseline[path]),
				LineDiff.SplitLines(_current.GetValueOrDefault(path, string.Empty)))) {
				SetPending(path, MapCurrentRangeToActual(path, hunk.AfterRange), true);
			}
			return true;
		}
	}

	// Drops a path from every tracked set after the file was deleted on revert. Caller holds _gate.
	private void Forget(string path) {
		_current.Remove(path);
		_baseline.Remove(path);
		_reviewBaseline.Remove(path);
		_acceptedAnchor.Remove(path);
		_preEdit.Remove(path);
		_createdSinceBaseline.Remove(path);
		_provenance.Remove(path);
	}

	/// <summary>
	/// A workspace-root-relative <c>path:line</c> jump target for the first line a just-recorded edit changed,
	/// with <c>/</c> separators so it's clickable on every platform. Always the full path from the workspace
	/// root — computed here, never echoed from the model or Claude's cwd — so <c>reveal-file</c> can resolve it.
	/// <see langword="null"/> for non-edit/non-PostToolUse events, notebooks, and no-op edits. Call after
	/// <see cref="Observe"/> folds in the PostToolUse event.
	/// </summary>
	/// <param name="value">The completed tool event whose mutation may yield a location.</param>
	public string? EditLocationFor(AgentEvent value) {
		ArgumentNullException.ThrowIfNull(value);
		return EditLocationsFor(value).FirstOrDefault();
	}

	/// <summary>The edit locations changed by a completed tool event, one per directly reported file.</summary>
	/// <param name="value">The completed tool event whose mutation may yield locations.</param>
	public IReadOnlyList<string> EditLocationsFor(AgentEvent value) {
		ArgumentNullException.ThrowIfNull(value);
		if (value is not AgentToolCompleted completed) {
			return [];
		}

		var locations = new List<string>();
		foreach (string path in ExtractEditLocationPaths(completed.Mutation)) {
			string before, after;
			lock (_gate) {
				if (!_current.TryGetValue(path, out string? current)) {
					continue;
				}
				after = current;
				before = _preEdit.GetValueOrDefault(path, string.Empty);
			}

			int? line = LineDiff.FirstChangedLine(before, after);
			if (line is not null) {
				locations.Add($"{Relativize(path)}:{line}");
			}
		}

		return locations;
	}

	/// <summary>The files whose current content differs from their session baseline.</summary>
	public IReadOnlyList<FileChange> Changes() {
		lock (_gate) {
			var changes = new List<FileChange>();
			foreach (var (path, current) in _current) {
				string baseline = _baseline.GetValueOrDefault(path, string.Empty);
				if (!string.Equals(baseline, current, StringComparison.Ordinal)) {
					changes.Add(new FileChange { Path = path, BaselineText = baseline, CurrentText = current });
				}
			}
			return changes;
		}
	}

	/// <summary>The change for a single <paramref name="path"/>, or <see langword="null"/> if it has no recorded change.</summary>
	/// <param name="path">Absolute file path.</param>
	public FileChange? Get(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			if (!_current.TryGetValue(path, out string? current)) {
				return null;
			}
			return new FileChange {
				Path = path,
				BaselineText = _baseline.GetValueOrDefault(path, string.Empty),
				CurrentText = current,
			};
		}
	}

	/// <summary>
	/// The inline review diff set: every file whose current content differs from its accepted anchor — so a
	/// fully-kept-but-uncommitted file (review baseline == current, but accepted anchor still behind) STAYS in the
	/// set to carry its faded band while other files are still pending. Keep-all — or resolving the set's last
	/// pending hunk (see <see cref="CommitAcceptedIfSettled"/>) — snaps the anchors to current and empties it.
	/// </summary>
	public IReadOnlyList<FileChange> TurnChanges() {
		lock (_gate) {
			var changes = new List<FileChange>();
			foreach (var (path, accepted) in _acceptedAnchor) {
				if (_current.TryGetValue(path, out string? current) && !string.Equals(accepted, current, StringComparison.Ordinal)) {
					changes.Add(new FileChange {
						Path = path,
						AcceptedBaselineText = accepted,
						BaselineText = _reviewBaseline.GetValueOrDefault(path, accepted),
						CurrentText = current,
					});
				}
			}

			return changes;
		}
	}

	/// <summary>
	/// The change for <paramref name="path"/> as the (accepted anchor, review baseline, current) triple, or
	/// <see langword="null"/> if the file isn't tracked. Any pair may be equal — the caller treats accepted ==
	/// current as "no markers", review baseline == current as "no pending hunks (faded only)".
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	public FileChange? GetTurn(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			if (!_reviewBaseline.TryGetValue(path, out string? baseline) || !_current.TryGetValue(path, out string? current)) {
				return null;
			}

			return new FileChange {
				Path = path,
				AcceptedBaselineText = _acceptedAnchor.GetValueOrDefault(path, baseline),
				BaselineText = baseline,
				CurrentText = current,
			};
		}
	}

	private string ReadOrEmpty(string path) => _fileSystem.FileExists(path) ? _fileSystem.ReadAllText(path) : string.Empty;

	private bool TryReadOrEmpty(string path, out string contents) {
		if (_fileSystem.FileExists(path)) {
			return _fileSystem.TryReadAllText(path, out contents);
		}

		contents = string.Empty;
		return true;
	}

	// Split text the way a Monaco model does (CRLF/CR normalized to LF), so the web's line ranges line up with Core's slices.
	private static List<string> SplitLines(string text) =>
		[.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n')];

	// Rejoin split lines with the EOL convention of the text they came from, so a single-hunk operation on a
	// CRLF file never rewrites the whole file (or its stored baseline) with LF endings.
	private static string JoinLines(IReadOnlyList<string> lines, string eolSource) =>
		string.Join(eolSource.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n", lines);

	// Slices a 1-based, end-exclusive range out of `lines`; false (empty slice) when out of bounds, which the
	// caller treats as a guard failure so an inconsistent request never writes.
	private static bool TryGetSlice(List<string> lines, LineRange range, out List<string> slice) {
		slice = [];
		if (range.Start < 1 || range.EndExclusive < range.Start || range.EndExclusive - 1 > lines.Count) {
			return false;
		}

		slice = lines.GetRange(range.Start - 1, range.EndExclusive - range.Start);
		return true;
	}

	private IReadOnlyList<string> ExtractEditPaths(AgentMutation mutation) {
		var paths = new List<string>();
		foreach (var file in ExtractFiles(mutation)) {
			if (ExtractEditPath(file) is { } path) {
				paths.Add(path);
			}
		}

		return paths;
	}

	private IReadOnlyList<string> ExtractEditLocationPaths(AgentMutation mutation) {
		var paths = new List<string>();
		foreach (var file in ExtractFiles(mutation)) {
			if (file.ProvidesEditLocation && ExtractEditPath(file) is { } path) {
				paths.Add(path);
			}
		}

		return paths;
	}

	private static IReadOnlyList<AgentMutation.File> ExtractFiles(AgentMutation mutation) =>
		mutation switch {
			AgentMutation.File file => [file],
			AgentMutation.Files files => files.Items,
			_ => [],
		};

	private string? ExtractEditPath(AgentMutation.File file) {
		try {
			return Resolve(file.Path, file.Cwd);
		} catch (ArgumentException) {
			// ToolInputJson is untrusted model output: malformed JSON or a malformed path is "not a trackable edit".
			return null;
		}
	}

	// A relative tool-input path resolves against the tool's cwd (where Claude ran it), falling back to the
	// workspace root when the event carries none — so a model-supplied partial path still lands on a real file.
	private string Resolve(string path, string? cwd) =>
		Path.IsPathRooted(path) ? path : Path.GetFullPath(path, string.IsNullOrEmpty(cwd) ? _workspaceRoot : cwd);

	// Path relative to the workspace root (never Claude's cwd, which drifts with `cd`) with '/' separators, so
	// the jump link matches what reveal-file resolves against; absolute for files outside it (e.g. scratch).
	private string Relativize(string absolutePath) {
		string relative = Path.GetRelativePath(_workspaceRoot, absolutePath);
		bool escapes = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative);
		return (escapes ? absolutePath : relative).Replace('\\', '/');
	}
}
