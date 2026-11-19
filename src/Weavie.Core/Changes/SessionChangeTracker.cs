using Weavie.Core.Agents;
using Weavie.Core.FileActivity;
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
	private readonly IFileActivitySink _fileActivity;
	private readonly string _workspaceRoot;
	private readonly Func<string, bool> _isInScope;
	private readonly Func<IReadOnlyList<string>, IReadOnlySet<string>> _ignoredAmong;
	private readonly object _gate = new();
	// Rendered turn summaries, memoized on the texts they were diffed from and rebuilt from the live set.
	private Dictionary<string, TurnChangeSummary> _summaries = new(PathIdentity.Comparer);
	private readonly TrackedMap<string> _current;
	// Absence is review data, including rejected creations and observed deletions.
	private readonly TrackedSet _missingCurrent;
	// Each held file's kept content: starts at its before and only an explicit Keep advances it (docs/specs/review-state.md).
	private readonly TrackedMap<string> _reviewBaseline;
	private readonly TrackedSet _missingReviewBaseline;
	// Each held file's before; holding a file means having one.
	private readonly TrackedMap<string> _acceptedAnchor;
	private readonly TrackedSet _missingAcceptedAnchor;
	// Each file's content at the most recent edit's PreToolUse; diffed against post-edit in EditLocationFor.
	private readonly TrackedMap<string> _preEdit;
	// The running tool's pre-edit capture per path, so its completion holds the right before even if a git review
	// closed between the two.
	private readonly Dictionary<string, (string Text, bool Exists)> _toolCapture = new(PathIdentity.Comparer);
	// Binary contents are never stored; an earlier text review remains retained but unavailable.
	private readonly Dictionary<string, FileStat> _nonText = new(PathIdentity.Comparer);
	/// <summary>Creates a tracker that reads files and reports their completed activity.</summary>
	/// <param name="fileSystem">The session filesystem the tracker reads changed-file content through.</param>
	/// <param name="fileActivity">The owning session's ordered file-activity sink.</param>
	/// <param name="workspaceRoot">
	/// The session's worktree root — the root <c>reveal-file</c> resolves relative paths against, so every
	/// <see cref="EditLocationFor"/> jump link is relative to it (never to Claude's drifting cwd).
	/// </param>
	/// <param name="isInScope">
	/// Predicate over an absolute path: only edits it accepts are tracked. See the type remarks.
	/// </param>
	public SessionChangeTracker(
		IFileSystem fileSystem,
		IFileActivitySink fileActivity,
		string workspaceRoot,
		Func<string, bool> isInScope) : this(fileSystem, fileActivity, workspaceRoot, isInScope,
			NothingIgnored, new MemoryReviewPersistence()) { }

	/// <summary>The ignore query for a worktree with no ignore rules.</summary>
	public static IReadOnlySet<string> NothingIgnored(IReadOnlyList<string> paths) => new HashSet<string>();

	/// <summary>
	/// Creates and restores a worktree's durable review before admitting agent edits; <c>ignoredAmong</c> answers which
	/// of the given worktree paths git ignores, and agent edits to those are never tracked.
	/// </summary>
	public SessionChangeTracker(
		IFileSystem fileSystem,
		IFileActivitySink fileActivity,
		string workspaceRoot,
		Func<string, bool> isInScope,
		Func<IReadOnlyList<string>, IReadOnlySet<string>> ignoredAmong,
		IReviewPersistence persistence) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(fileActivity);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentNullException.ThrowIfNull(isInScope);
		ArgumentNullException.ThrowIfNull(ignoredAmong);
		_fileSystem = fileSystem;
		_fileActivity = fileActivity;
		_workspaceRoot = NormalizePath(workspaceRoot);
		_isInScope = isInScope;
		_ignoredAmong = ignoredAmong;
		_current = new(PathIdentity.Comparer, FileChanged);
		_reviewBaseline = new(PathIdentity.Comparer, FileChanged);
		_acceptedAnchor = new(PathIdentity.Comparer, FileChanged);
		_preEdit = new(PathIdentity.Comparer, FileChanged);
		_missingCurrent = new(PathIdentity.Comparer, FileChanged);
		_missingReviewBaseline = new(PathIdentity.Comparer, FileChanged);
		_missingAcceptedAnchor = new(PathIdentity.Comparer, FileChanged);
		_provenance = new(PathIdentity.Comparer, FileChanged);
		_conversationPrompts = new(StringComparer.Ordinal, key => _dirtyPrompts.Add(key));
		_undoStack = new(this, true);
		_redoStack = new(this, false);
		_persistence = persistence;
		RestoreCheckpoint();
	}

	/// <summary>
	/// Accumulates provider-reported edits without changing earlier review decisions at prompt boundaries.
	/// </summary>
	public void Observe(AgentEvent value) {
		ArgumentNullException.ThrowIfNull(value);
		string conversationId = value is AgentConversationEvent side ? side.ConversationId : string.Empty;
		if (value is AgentConversationEvent scoped) value = scoped.Value;
		if (value is AgentConversationRemoved removed) {
			lock (_gate) {
				if (_conversationPrompts.Remove(removed.ConversationId)) Checkpoint();
			}
			return;
		}

		if (value is AgentPromptSubmitted submitted) {
			lock (_gate) {
				_conversationPrompts[conversationId] = submitted.Prompt;
			}

			return;
		}

		if (value is AgentTurnStopped) {
			// A tool that never completed leaves no capture for a later edit to mistake for its before.
			lock (_gate) _toolCapture.Clear();
			return;
		}

		// Workspace-wide tools can delete an already-tracked path without reporting it; a git review re-lists instead.
		if (value is AgentToolCompleted && Review is null) {
			ReconcileDeletions();
		}

		var mutation = value switch {
			AgentToolStarting starting => starting.Mutation,
			AgentToolCompleted completed => completed.Mutation,
			_ => new AgentMutation.None(),
		};
		var paths = AdmitEditPaths(ExtractEditPaths(mutation));
		if (paths.Count == 0) {
			return;
		}

		if (value is AgentToolStarting) {
			foreach (string path in paths) {
				CaptureBaseline(path);
			}
		} else if (value is AgentToolCompleted) {
			foreach (string path in paths) {
				RecordChange(path, conversationId);
			}
		}
	}


	/// <summary>Snapshots <paramref name="path"/>'s pre-edit content; outside a git review it becomes the file's before, once.</summary>
	/// <param name="path">Absolute file path.</param>
	public void CaptureBaseline(string path) {
		path = NormalizePath(path);
		lock (_gate) {
			// Disk content here is the pre-edit file. Seed baselines if missing; track existence so a later revert
			// deletes rather than truncates a file that didn't yet exist.
			bool existed = _fileSystem.FileExists(path);
			if (!TryReadOrEmpty(path, out string content)) {
				SuspendReview(path);
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
		_preEdit[path] = content;
		_toolCapture[path] = (content, existed);
		// A git review's befores come from git; the edit joins it once reported.
		if (_review is not null) {
			Checkpoint();
			return;
		}
		if (_reviewBaseline.TryAdd(path, content) && !existed) {
			_missingReviewBaseline.Add(path);
		}

		if (_acceptedAnchor.TryAdd(path, content) && !existed) {
			_missingAcceptedAnchor.Add(path);
		}
		if (_current.TryAdd(path, content)) SetMissing(_missingCurrent, path, !existed);
		Checkpoint();
	}

	/// <summary>Records <paramref name="path"/>'s latest content (baselining to empty if it appeared this session).</summary>
	/// <param name="path">Absolute file path.</param>
	public void RecordChange(string path) => RecordChange(path, string.Empty);

	private void RecordChange(string path, string conversationId) {
		path = NormalizePath(path);
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
				SuspendReview(path);
			} else {
				ignoredNonText = false;
				_missingCurrent.Remove(path);
				if (_nonText.Remove(path)) {
					// A binary baseline cannot support a safe line-level reject; refresh the now-text file without review.
					reviewRemoved = false;
					nonTextChanged = true;
				} else if (_review is not null) {
					reviewRemoved = false;
					nonTextChanged = false;
					string before = _preEdit.GetValueOrDefault(path, content);
					RecordAgentProvenance(path, before, content, content, _conversationPrompts.GetValueOrDefault(conversationId));
				} else {
					reviewRemoved = false;
					nonTextChanged = false;
					// An unheld file takes this tool's pre-edit capture as its before; with none, it appeared this session.
					if (!_acceptedAnchor.ContainsKey(path)) {
						var (text, exists) = _toolCapture.GetValueOrDefault(path, (string.Empty, false));
						HoldBefore(path, text, exists);
					}
					string before = _preEdit.GetValueOrDefault(path, _current.GetValueOrDefault(path, string.Empty));
					string reviewCurrent = _current.GetValueOrDefault(path, before);
					_current[path] = RecordAgentProvenance(path, before, content, reviewCurrent, _conversationPrompts.GetValueOrDefault(conversationId));
				}
			}
			_toolCapture.Remove(path);
			Checkpoint();
		}

		if (!ignoredNonText || nonTextChanged || reviewRemoved) {
			ReportCurrentState(path);
		}
	}

	/// <summary>
	/// Retains missing files as review data and reports each completed deletion.
	/// </summary>
	private void ReconcileDeletions() {
		List<string>? removed = null;
		lock (_gate) {
			foreach (string path in new List<string>(_current.Keys)) {
				if (!_missingCurrent.Contains(path) && !_fileSystem.FileExists(path)) {
					_current[path] = string.Empty;
					_missingCurrent.Add(path);
					CommitReviewProvenance(path);
					(removed ??= []).Add(path);
				}
			}

			foreach (string path in new List<string>(_nonText.Keys)) {
				if (!_fileSystem.FileExists(path)) {
					_nonText.Remove(path);
					(removed ??= []).Add(path);
				}
			}
			if (removed is not null) Checkpoint();
		}

		if (removed is null) {
			return;
		}

		foreach (string path in removed) {
			_fileActivity.ReportDeleted(path);
		}
	}

	/// <summary>
	/// Reverts a single hunk on disk, sourcing replacement text from Core's review baseline (never from message
	/// content — the hook-bridge security rule). <paramref name="guardText"/> is an optimistic-concurrency check:
	/// a mismatch against the file's current lines aborts the write (<see cref="RevertHunkOutcome.GuardMismatch"/>).
	/// A created-since-baseline file the revert empties is deleted (<see cref="RevertHunkOutcome.Deleted"/>), not truncated.
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	/// <param name="baselineRevision">The review baseline revision the web's ranges were computed against.</param>
	/// <param name="baselineRange">The hunk's range in the review baseline (1-based, end-exclusive).</param>
	/// <param name="currentRange">The hunk's range in the current file (1-based, end-exclusive).</param>
	/// <param name="guardText">The exact current text of <paramref name="currentRange"/> as the web sees it.</param>
	public RevertHunkOutcome RevertHunk(
		string path,
		string? baselineRevision,
		LineRange baselineRange,
		LineRange currentRange,
		string guardText) {
		ArgumentNullException.ThrowIfNull(guardText);
		if (!TryScope(path, out path)) {
			return RevertHunkOutcome.GuardMismatch;
		}

		List<CorrectionEdit> edits;
		RevertHunkOutcome outcome;
		lock (_gate) {
			ReconcilePath(path);
			var baselineLines = SplitLines(_reviewBaseline.GetValueOrDefault(path, string.Empty));
			if (!ReviewBaselineIs(path, baselineRevision)
				|| !TryGetSlice(baselineLines, baselineRange, out var replacement)
				|| TrySplice(path, currentRange, guardText, replacement) is not { } spliced) {
				return RevertHunkOutcome.GuardMismatch;
			}

			// The rejected hunk is the correction: the agent's lines out, the baseline's back in.
			edits = CorrectionsForRevert(path, spliced.CurrentRange, baselineRange);
			var before = Capture(path, withDisk: true);
			// Reverting the last hunk to an absent baseline returns it to non-existence.
			string diskContent = ApplyReviewChange(path, spliced.CurrentRaw, spliced.NewContent);
			if (diskContent.Length == 0 && _missingReviewBaseline.Contains(path)) {
				_fileSystem.DeleteFile(path);
				_current[path] = string.Empty;
				_missingCurrent.Add(path);
				outcome = RevertHunkOutcome.Deleted;
			} else {
				_fileSystem.WriteAllText(path, diskContent);
				_current[path] = spliced.NewContent;
				_missingCurrent.Remove(path);
				outcome = RevertHunkOutcome.Reverted;
			}

			CommitReviewProvenance(path);
			Record(ReviewActionKind.Revert, touchesDisk: true, currentRange.Start, [before]);
			ReportCurrentState(path);
		}

		RaiseCorrected(edits);
		return outcome;
	}

	/// <summary>
	/// Reverts a whole file to its review baseline (per-file undo). A created-since-baseline file is deleted, not
	/// truncated. No guard — the whole file is reset, not a single hunk against concurrent edits.
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	public RevertHunkOutcome RevertFile(string path) {
		if (!TryScope(path, out path)) {
			return RevertHunkOutcome.GuardMismatch;
		}

		List<CorrectionEdit> edits;
		RevertHunkOutcome outcome;
		lock (_gate) {
			if (!ShownMatchesDisk([path])) return RevertHunkOutcome.Unlisted;
			ReconcilePath(path);
			if (_nonText.ContainsKey(path) || !_reviewBaseline.ContainsKey(path)) {
				return RevertHunkOutcome.GuardMismatch;
			}

			edits = RevertCorrections(path);
			var before = Capture(path, withDisk: true);
			outcome = RevertFileLocked(path);
			Record(ReviewActionKind.Revert, touchesDisk: true, line: null, [before]);
		}

		RaiseCorrected(edits);
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
			if (!ShownMatchesDisk(_acceptedAnchor.Keys)) return ReviewHistoryResult.Blocked(true);
			ReconcileReviewDisk();
			var paths = new List<string>();
			foreach (var (path, baseline) in _reviewBaseline) {
				if (!_nonText.ContainsKey(path) && _current.TryGetValue(path, out string? current)
					&& (!string.Equals(baseline, current, StringComparison.Ordinal)
						|| _missingReviewBaseline.Contains(path) != _missingCurrent.Contains(path))) {
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
			int completed = 0;
			try {
				foreach (string path in paths) {
					RevertFileLocked(path);
					completed++;
				}
			} finally {
				Record(ReviewActionKind.Revert, touchesDisk: true, line: null, before.Take(completed).ToArray());
			}
			result = ReviewHistoryResult.Done(true, paths, null);
		}

		RaiseCorrected(edits);
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
		if (_missingReviewBaseline.Contains(path)) {
			_fileSystem.DeleteFile(path);
			_current[path] = string.Empty;
			_missingCurrent.Add(path);
			CommitReviewProvenance(path);
			_fileActivity.ReportDeleted(path);
			return RevertHunkOutcome.Deleted;
		}

		_fileSystem.WriteAllText(path, diskContent);
		_current[path] = baseline;
		_missingCurrent.Remove(path);
		CommitReviewProvenance(path);
		ReportCurrentState(path);
		return RevertHunkOutcome.Reverted;
	}

	/// <summary>
	/// Keeps a single hunk: advances the file's review baseline over just that hunk so it leaves the pending diff
	/// for good (and survives session switches), without touching disk. <paramref name="guardText"/> is the same
	/// optimistic-concurrency check as <see cref="RevertHunk"/>; a mismatch aborts and returns <see langword="false"/>.
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	/// <param name="baselineRevision">The review baseline revision the web's ranges were computed against.</param>
	/// <param name="baselineRange">The hunk's range in the review baseline (1-based, end-exclusive).</param>
	/// <param name="currentRange">The hunk's range in the current file (1-based, end-exclusive).</param>
	/// <param name="guardText">The exact current text of <paramref name="currentRange"/> as the web sees it.</param>
	public bool KeepHunk(
		string path,
		string? baselineRevision,
		LineRange baselineRange,
		LineRange currentRange,
		string guardText) {
		ArgumentNullException.ThrowIfNull(guardText);
		if (!TryScope(path, out path)) {
			return false;
		}

		lock (_gate) {
			// currentRange + guardText are in the live-model (== disk) space the web diffed, so guard and take the
			// kept lines straight from disk — never remap through _current, which omits the user's non-agent edits.
			ReconcilePath(path);
			if (_nonText.ContainsKey(path) || !ReviewBaselineIs(path, baselineRevision)) return false;
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
			if (string.Equals(_reviewBaseline[path], _current.GetValueOrDefault(path, string.Empty), StringComparison.Ordinal)) {
				SetMissing(_missingReviewBaseline, path, _missingCurrent.Contains(path));
			}
			SetPending(path, [currentRange], false);
			Record(ReviewActionKind.Keep, touchesDisk: false, currentRange.Start, [before]);
			return true;
		}
	}

	/// <summary>
	/// Keeps a whole file: advances its kept text to current content so the file leaves the pending diff, without
	/// touching disk. No-op for a path the review doesn't hold.
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	public void KeepFile(string path) {
		if (!TryScope(path, out path)) {
			return;
		}

		lock (_gate) {
			// A git review keeps what it shows; a change it hasn't listed yet stays pending.
			if (_review is null) ReconcilePath(path);
			if (!_nonText.ContainsKey(path) && _current.ContainsKey(path)) KeepLocked([path]);
			// A keep retried after its save failed finds the review already scrapped in memory; this saves that.
			else Checkpoint();
		}
	}

	/// <summary>Keeps every listed file as one undoable step.</summary>
	public void KeepAll() {
		lock (_gate) {
			if (_review is null) ReconcileReviewDisk();
			KeepLocked([.. TurnChangesLocked().Select(change => change.Path)]);
		}
	}

	private void KeepLocked(IReadOnlyList<string> paths) {
		var before = new List<PathState>();
		foreach (string path in paths) {
			var state = Capture(path, withDisk: false);
			_reviewBaseline[path] = state.Current;
			SetMissing(_missingReviewBaseline, path, !state.CurrentExists);
			SetAllPending(path, false);
			// A no-op keep (already kept) records nothing, so its undo wouldn't surprise with an empty step.
			if (state.ReviewBaseline != state.Current || state.ReviewBaselineExists != state.CurrentExists) before.Add(state);
		}
		if (before.Count > 0) {
			Record(ReviewActionKind.Keep, touchesDisk: false, line: null, before);
		} else {
			CompleteAcceptanceLocked();
			Checkpoint();
		}
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
		ArgumentNullException.ThrowIfNull(acceptedGuardText);
		ArgumentNullException.ThrowIfNull(guardText);
		if (!TryScope(path, out path)) {
			return false;
		}

		lock (_gate) {
			string reviewRaw = _reviewBaseline.GetValueOrDefault(path, string.Empty);
			if (reviewRaw == _acceptedAnchor.GetValueOrDefault(path, string.Empty)
				&& _missingReviewBaseline.Contains(path) == _missingAcceptedAnchor.Contains(path)) return false;
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
			if (string.Equals(_reviewBaseline[path], _acceptedAnchor.GetValueOrDefault(path, string.Empty), StringComparison.Ordinal)) {
				SetMissing(_missingReviewBaseline, path, _missingAcceptedAnchor.Contains(path));
			}
			MarkReviewPending(path);
			SynchronizeHistory(external: false, except: null);
			Checkpoint();
			return true;
		}
	}

	// Removes a path from the review, leaving its agent provenance. Caller holds _gate.
	private void ForgetReview(string path) {
		_current.Remove(path);
		_missingCurrent.Remove(path);
		_reviewBaseline.Remove(path);
		_missingReviewBaseline.Remove(path);
		_acceptedAnchor.Remove(path);
		_missingAcceptedAnchor.Remove(path);
	}

	private void SuspendReview(string path) {
		foreach (var action in HistoryFor(path))
			action.ReplacePatches(path, [.. action.PatchesFor(path).Select(patch => patch with { Stale = true })]);
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
				if (!_provenance.TryGetValue(path, out var provenance)) {
					continue;
				}
				after = provenance.Text;
				before = _preEdit.GetValueOrDefault(path, string.Empty);
			}

			int? line = LineDiff.FirstChangedLine(before, after);
			if (line is not null) {
				locations.Add($"{Relativize(path)}:{line}");
			}
		}

		return locations;
	}

	/// <summary>
	/// The inline review diff set: every held file whose current state differs from its before, kept or not, or that
	/// carries a rejection the user can still undo.
	/// </summary>
	public IReadOnlyList<FileChange> TurnChanges() {
		lock (_gate) {
			return TurnChangesLocked();
		}
	}

	/// <summary>
	/// Every turn change with the counts the review navigator renders. Each file's summary is memoized on the
	/// exact texts it was diffed from, so re-reading the set after one file changes costs one file's diff rather
	/// than the whole turn's — this is read on every editor save.
	/// </summary>
	public IReadOnlyList<TurnChangeSummary> TurnChangeSummaries() {
		lock (_gate) {
			var live = new Dictionary<string, TurnChangeSummary>(PathIdentity.Comparer);
			var summaries = new List<TurnChangeSummary>();
			foreach (var change in TurnChangesLocked()) {
				if (!_summaries.TryGetValue(change.Path, out var summary) || !summary.Describes(change)) {
					summary = TurnChangeSummary.For(change);
				}

				live[change.Path] = summary;
				summaries.Add(summary);
			}

			// Rebuilt from the live set, so a file that left the turn takes its retained text with it.
			_summaries = live;
			return summaries;
		}
	}

	// The turn change set. Caller holds _gate.
	private List<FileChange> TurnChangesLocked() {
		var changes = new List<FileChange>();
		foreach (var (path, accepted) in _acceptedAnchor) {
			if (_nonText.ContainsKey(path)) continue;
			if (_current.TryGetValue(path, out string? current)
				&& (!string.Equals(accepted, current, StringComparison.Ordinal)
					|| _missingAcceptedAnchor.Contains(path) != _missingCurrent.Contains(path)
					|| RejectedFor(path).Count > 0)) {
				changes.Add(new FileChange {
					Path = path,
					AcceptedBaselineText = accepted,
					BaselineText = _reviewBaseline.GetValueOrDefault(path, accepted),
					CurrentText = current,
					AcceptedBaselineExists = !_missingAcceptedAnchor.Contains(path),
					BaselineExists = !_missingReviewBaseline.Contains(path),
					CurrentExists = !_missingCurrent.Contains(path),
				});
			}
		}

		return changes;
	}

	/// <summary>
	/// The change for <paramref name="path"/> as the (accepted anchor, review baseline, current) triple, or
	/// <see langword="null"/> if the file isn't tracked. Any pair may be equal — the caller treats accepted ==
	/// current in text and existence as "no markers", and the same review/current state as "faded only".
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	public FileChange? GetTurn(string path) {
		path = NormalizePath(path);
		lock (_gate) {
			if (_nonText.ContainsKey(path)) return null;
			if (!_reviewBaseline.TryGetValue(path, out string? baseline) || !_current.TryGetValue(path, out string? current)) {
				return null;
			}

			return new FileChange {
				Path = path,
				Rejected = RejectedFor(path),
				AcceptedBaselineText = _acceptedAnchor.GetValueOrDefault(path, baseline),
				BaselineText = baseline,
				CurrentText = current,
				AcceptedBaselineExists = !_missingAcceptedAnchor.Contains(path),
				BaselineExists = !_missingReviewBaseline.Contains(path),
				CurrentExists = !_missingCurrent.Contains(path),
			};
		}
	}

	/// <summary>
	/// The review baseline or accepted anchor text of <paramref name="path"/> whose <see cref="TextRevision"/> is
	/// <paramref name="revision"/>, or <see langword="null"/> once neither holds it (a newer turn diff supersedes it).
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	/// <param name="revision">The revision a <see cref="ChangeMessages.TurnDiff"/> named.</param>
	public string? ReviewText(string path, string revision) {
		path = NormalizePath(path);
		ArgumentNullException.ThrowIfNull(revision);
		lock (_gate) {
			if (_nonText.ContainsKey(path) || !_reviewBaseline.TryGetValue(path, out string? baseline)) return null;
			string accepted = _acceptedAnchor.GetValueOrDefault(path, baseline);
			return TextRevision.Of(baseline) == revision ? baseline
				: TextRevision.Of(accepted) == revision ? accepted
				: null;
		}
	}

	// Whether the review baseline is still the one the web diffed: guarding only the current side would let ranges
	// computed against an older baseline splice the wrong lines. Caller holds _gate.
	private bool ReviewBaselineIs(string path, string? revision) =>
		TextRevision.Of(_reviewBaseline.GetValueOrDefault(path, string.Empty), !_missingReviewBaseline.Contains(path)) == revision;

	private static void SetMissing(TrackedSet missing, string path, bool value) {
		if (value) {
			missing.Add(path);
		} else {
			missing.Remove(path);
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

	private void ReportCurrentState(string path) {
		if (_fileSystem.TryGetStat(path, out var revision) && revision.Exists) {
			_fileActivity.ReportChanged(path, revision);
		} else {
			_fileActivity.ReportDeleted(path);
		}
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

	// `range` + `guardText` are in the live-model (== disk) space the web diffed, so guard against disk directly;
	// the range then maps into _current space (which omits the user's non-agent edits) so the splice rewrites only
	// the agent's lines. Null on any guard or bounds failure, so no caller writes against a stale request.
	private SplicedContent? TrySplice(string path, LineRange range, string guardText, IReadOnlyList<string> replacement) {
		if (_nonText.ContainsKey(path) || !TryGetSlice(SplitLines(ReadOrEmpty(path)), range, out var guarded)
			|| !string.Equals(string.Join("\n", guarded), guardText, StringComparison.Ordinal)) {
			return null;
		}

		string currentRaw = _current.GetValueOrDefault(path, ReadOrEmpty(path));
		var currentLines = SplitLines(currentRaw);
		var inCurrent = MapActualRangeToCurrent(path, range);
		if (!TryGetSlice(currentLines, inCurrent, out _)) {
			return null;
		}

		var newLines = new List<string>(currentLines);
		newLines.RemoveRange(inCurrent.Start - 1, inCurrent.EndExclusive - inCurrent.Start);
		newLines.InsertRange(inCurrent.Start - 1, replacement);
		return new SplicedContent(currentRaw, JoinLines(newLines, currentRaw), inCurrent);
	}

	// A guarded splice's inputs and result: the pre-change content, the spliced content, and the range in _current space.
	private readonly record struct SplicedContent(string CurrentRaw, string NewContent, LineRange CurrentRange);

	// In-scope paths, minus untracked ones git ignores (e.g. Claude's own .claude/worktrees/): an already-tracked
	// path stays admitted, so only a file's first edit asks git.
	private List<string> AdmitEditPaths(IReadOnlyList<string> reported) {
		var paths = reported.Where(_isInScope).Select(NormalizePath).Distinct(PathIdentity.Comparer).ToList();
		List<string> candidates;
		lock (_gate) {
			candidates = [.. paths.Where(path => !_current.ContainsKey(path) && !_nonText.ContainsKey(path)
				&& PathBoundary.Contains(_workspaceRoot, path))];
		}

		if (candidates.Count == 0) return paths;
		var ignored = _ignoredAmong(candidates);
		return ignored.Count == 0 ? paths : [.. paths.Where(path => !ignored.Contains(path))];
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

	private string Resolve(string path, string? cwd) => AgentPath.Resolve(path, cwd, _workspaceRoot);

	// Every path-taking review action enters here. The guard text of a hunk revert is checked against disk, not
	// against the review baseline, so an out-of-scope path with a matching guard would splice a file review never
	// tracked — the scope predicate is enforced here rather than at each caller.
	private bool TryScope(string path, out string normalized) {
		normalized = NormalizePath(path);
		return _isInScope(normalized);
	}

	private static string NormalizePath(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		return Path.GetFullPath(path);
	}

	// Path relative to the workspace root (never Claude's cwd, which drifts with `cd`) with '/' separators, so
	// the jump link matches what reveal-file resolves against; absolute for files outside it (e.g. scratch).
	private string Relativize(string absolutePath) {
		string relative = Path.GetRelativePath(_workspaceRoot, absolutePath);
		bool escapes = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative);
		return (escapes ? absolutePath : relative).Replace('\\', '/');
	}
}
