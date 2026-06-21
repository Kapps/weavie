using System.Text.Json;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;

namespace Weavie.Core.Changes;

/// <summary>
/// Records every file changed during the session and the diff against each file's <em>session baseline</em>
/// (its content the first time a tool touched it). Fed by the hook stream — <see cref="Observe"/> hooks onto
/// <c>HookBridgeServer.Observed</c> — which fires for edits in <em>every</em> permission mode (the hook runs
/// before the permission check), so the change feed is independent of openDiff and the mode. PreToolUse
/// snapshots the pristine baseline; PostToolUse (the edit having landed) records the new content. Read on the
/// host's UI thread for the changes view; mutated from the hook accept loop — hence the lock.
/// </summary>
public sealed class SessionChangeTracker {
	private readonly IFileSystem _fileSystem;
	private readonly object _gate = new();
	private readonly Dictionary<string, string> _baseline = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _current = new(StringComparer.Ordinal);
	// REVIEW baseline: each file's last-reviewed content. Seeded the first time a file is touched and advanced
	// ONLY on an explicit keep-all (AcceptTurn) or a per-hunk revert — NOT on a turn boundary. This is the
	// accumulate model (docs/specs/turn-review.md): the review set is everything Claude changed that you
	// haven't acknowledged, persisting across as many turns as you like, so the inline diff renders against
	// this baseline (TurnChanges/GetTurn) rather than a per-turn snapshot.
	private readonly Dictionary<string, string> _reviewBaseline = new(StringComparer.Ordinal);
	// Per-EDIT pre-state: each file's content captured at the PreToolUse of the most recent edit, overwritten
	// every edit (unlike the session/review baselines, which stick). Diffed against the post-edit content in
	// EditLocationFor to point at the line THIS edit changed, even on the 2nd+ edit of a file within a turn.
	private readonly Dictionary<string, string> _preEdit = new(StringComparer.Ordinal);
	// Files that did NOT exist on disk when their review baseline was first captured (created since the
	// baseline). Reverting the last hunk of such a file returns it to its baseline state — which is
	// non-existence, not emptiness — so the revert DELETES the file rather than leaving a 0-byte one. Deletion
	// keys off existence-at-baseline, not emptiness (a genuinely empty file that existed at baseline is kept).
	private readonly HashSet<string> _createdSinceBaseline = new(StringComparer.Ordinal);

	/// <summary>Creates a tracker that reads file content through <paramref name="fileSystem"/>.</summary>
	/// <param name="fileSystem">The filesystem seam used to snapshot baseline + current content.</param>
	public SessionChangeTracker(IFileSystem fileSystem) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
	}

	/// <summary>Raised whenever the change set updates (a file's current content was recorded).</summary>
	public event Action? Changed;

	/// <summary>
	/// Raised with the absolute path of a file whose content was just recorded, so the host can push a
	/// targeted live-refresh of that one file's editor model. Fires alongside <see cref="Changed"/>.
	/// </summary>
	public event Action<string>? FileChanged;

	/// <summary>
	/// Raised with the absolute path of a tracked file that has disappeared from disk — deleted by a tool that
	/// isn't an editor (a <c>Bash</c> rm, a rename, a generated-then-cleaned-up temp) — so the host can close its
	/// editor tab. Fires from <see cref="Observe"/>'s post-tool reconciliation, alongside a single <see cref="Changed"/>.
	/// </summary>
	public event Action<string>? FileDeleted;

	/// <summary>
	/// Folds a hook event into the change set: PreToolUse on a file-editing tool snapshots the baseline,
	/// PostToolUse records the new content. A non-editing tool (Bash, etc.) records no edit, but its PostToolUse
	/// still triggers a disk reconciliation (<see cref="ReconcileDeletions"/>) so a file Claude removed mid-turn —
	/// e.g. a <c>Bash</c> rm of a scratch file it just created — leaves the review set instead of stranding the
	/// ← / → walk on a path that no longer exists. Turn boundaries are ignored — the review baseline is the
	/// accumulate baseline now, so it never resets on a new prompt.
	/// </summary>
	/// <param name="request">The observed hook event.</param>
	public void Observe(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);

		// A PostToolUse settles a completed tool: reconcile FIRST so a deletion by any tool (the edit tools never
		// delete; a Bash rm / mv does) drops the vanished file before we record this tool's own edit (if any).
		if (request.Event == HookEventKind.PostToolUse) {
			ReconcileDeletions();
		}

		string? path = ExtractEditPath(request);
		if (path is null) {
			return;
		}

		if (request.Event == HookEventKind.PreToolUse) {
			CaptureBaseline(path);
		} else if (request.Event == HookEventKind.PostToolUse) {
			RecordChange(path);
		}
	}

	/// <summary>
	/// Accepts the whole review set (Keep-all): advances every tracked file's review baseline to its current
	/// content, clearing the inline review diff. The session diff (vs the original session baseline) is kept.
	/// Everything now sits at its baseline, so nothing counts as "created since baseline" any more.
	/// </summary>
	public void AcceptTurn() {
		lock (_gate) {
			_reviewBaseline.Clear();
			foreach (var (path, content) in _current) {
				_reviewBaseline[path] = content;
			}

			_createdSinceBaseline.Clear();
		}
	}

	/// <summary>Snapshots <paramref name="path"/>'s current content as its session + review baseline, once.</summary>
	/// <param name="path">Absolute file path.</param>
	public void CaptureBaseline(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			// Disk content here = the file before this edit. Seed the session baseline (first touch ever) and
			// the review baseline (first review-touch) if either is missing, and always record it as the
			// per-edit pre-state (overwritten each edit) so EditLocationFor can pinpoint this edit's line. If the
			// file doesn't exist yet (about to be created), remember that so a later revert deletes rather than
			// truncates it.
			bool existed = _fileSystem.FileExists(path);
			string content = ReadOrEmpty(path);
			_baseline.TryAdd(path, content);
			if (_reviewBaseline.TryAdd(path, content) && !existed) {
				_createdSinceBaseline.Add(path);
			}

			_preEdit[path] = content;
		}
	}

	/// <summary>Records <paramref name="path"/>'s latest content (baselining to empty if it appeared this session).</summary>
	/// <param name="path">Absolute file path.</param>
	public void RecordChange(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			_baseline.TryAdd(path, string.Empty);
			// First review-touch with no prior CaptureBaseline ⇒ the file appeared this session without a
			// pre-snapshot, so it didn't exist at baseline (created since baseline).
			if (_reviewBaseline.TryAdd(path, string.Empty)) {
				_createdSinceBaseline.Add(path);
			}

			_current[path] = ReadOrEmpty(path);
		}

		Changed?.Invoke();
		FileChanged?.Invoke(path);
	}

	/// <summary>
	/// Reconciles the tracked set against disk: any file that's been recorded but no longer exists was deleted out
	/// from under us (a <c>Bash</c> rm, a rename, a generated-then-cleaned-up temp). Such a file can't be rendered
	/// in the inline review — there's nothing on disk to open — so it's dropped from tracking entirely, mirroring
	/// the revert-deletes-a-created-file path (<see cref="Forget"/>). Raises <see cref="FileDeleted"/> per removed
	/// path (so the host closes its editor tab + clears the marker) and a single <see cref="Changed"/> so the
	/// review walk re-pushes without the dead entries. A no-op (no events) when nothing vanished.
	/// </summary>
	private void ReconcileDeletions() {
		List<string>? removed = null;
		lock (_gate) {
			// Snapshot keys first: Forget mutates _current while we iterate. Only recorded files (in _current) can
			// appear in the review walk, so reconciling that set is both necessary and sufficient.
			foreach (string path in new List<string>(_current.Keys)) {
				if (!_fileSystem.FileExists(path)) {
					Forget(path);
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
	/// Reverts a single hunk on disk, sourcing the replacement text from Core's own review baseline (never from
	/// content supplied over a message — the hook-bridge security rule). The web sends 1-based, end-exclusive
	/// line ranges plus <paramref name="guardText"/> — the exact current text of the hunk as the web sees it, an
	/// optimistic-concurrency check. If the file's current lines no longer match <paramref name="guardText"/> (a
	/// parallel agent or a later Claude/user edit moved the file), the revert ABORTS without writing
	/// (<see cref="RevertHunkOutcome.GuardMismatch"/>) rather than clobbering the concurrent edit. On a match the
	/// hunk's current lines are replaced by the baseline lines; a created-since-baseline file whose revert empties
	/// it is DELETED (<see cref="RevertHunkOutcome.Deleted"/>) rather than truncated to a 0-byte file.
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	/// <param name="baselineRange">The hunk's range in the review baseline (1-based, end-exclusive).</param>
	/// <param name="currentRange">The hunk's range in the current file (1-based, end-exclusive).</param>
	/// <param name="guardText">The exact current text of <paramref name="currentRange"/> as the web sees it.</param>
	public RevertHunkOutcome RevertHunk(string path, LineRange baselineRange, LineRange currentRange, string guardText) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		ArgumentNullException.ThrowIfNull(guardText);
		lock (_gate) {
			var currentLines = SplitLines(ReadOrEmpty(path));
			if (!TryGetSlice(currentLines, currentRange, out var currentSlice)
				|| !string.Equals(string.Join("\n", currentSlice), guardText, StringComparison.Ordinal)) {
				return RevertHunkOutcome.GuardMismatch;
			}

			var baselineLines = SplitLines(_reviewBaseline.GetValueOrDefault(path, string.Empty));
			if (!TryGetSlice(baselineLines, baselineRange, out var replacement)) {
				return RevertHunkOutcome.GuardMismatch;
			}

			var newLines = new List<string>(currentLines);
			newLines.RemoveRange(currentRange.Start - 1, currentRange.EndExclusive - currentRange.Start);
			newLines.InsertRange(currentRange.Start - 1, replacement);
			string newContent = string.Join("\n", newLines);

			// Reverting the last hunk of a created file returns it to non-existence — delete it (matching the
			// per-file + whole-set reverts), dropping it from tracking entirely.
			if (newContent.Length == 0 && _createdSinceBaseline.Contains(path)) {
				_fileSystem.DeleteFile(path);
				Forget(path);
				return RevertHunkOutcome.Deleted;
			}

			_fileSystem.WriteAllText(path, newContent);
			_current[path] = newContent;
			return RevertHunkOutcome.Reverted;
		}
	}

	/// <summary>
	/// Reverts a whole file to its review baseline (the whole-set / per-file undo). Like <see cref="RevertHunk"/>
	/// it owns the delete-vs-truncate decision in one place: a file created since its baseline is DELETED rather
	/// than truncated to a 0-byte file; any other file is rewritten to its baseline content. No guard — the whole
	/// file is being reset, not a single hunk against concurrent edits.
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	public RevertHunkOutcome RevertFile(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			string baseline = _reviewBaseline.GetValueOrDefault(path, string.Empty);
			if (baseline.Length == 0 && _createdSinceBaseline.Contains(path)) {
				_fileSystem.DeleteFile(path);
				Forget(path);
				return RevertHunkOutcome.Deleted;
			}

			_fileSystem.WriteAllText(path, baseline);
			_current[path] = baseline;
			return RevertHunkOutcome.Reverted;
		}
	}

	// Drops a path from every tracked set after the file was deleted on revert. Caller holds _gate (the lock is
	// re-entrant), so this is gate-safe whether reached from RevertHunk or RevertFile.
	private void Forget(string path) {
		_current.Remove(path);
		_baseline.Remove(path);
		_reviewBaseline.Remove(path);
		_preEdit.Remove(path);
		_createdSinceBaseline.Remove(path);
	}

	/// <summary>
	/// A workspace-relative <c>path:line</c> reference to the first line a just-recorded edit changed, for
	/// surfacing as a clickable jump target after the edit lands (the terminal makes <c>path:line</c> tokens
	/// clickable). Call after <see cref="Observe"/> has folded in the PostToolUse event. Returns
	/// <see langword="null"/> for non-edit / non-PostToolUse events, for notebooks (no meaningful text line),
	/// and when the edit changed no line. Paths use <c>/</c> separators so the reference is clickable on every
	/// platform.
	/// </summary>
	/// <param name="request">The observed hook event (only PostToolUse edits yield a location).</param>
	public string? EditLocationFor(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);
		if (request.Event != HookEventKind.PostToolUse
			|| request.ToolName is not ("Edit" or "Write" or "MultiEdit")) {
			return null;
		}

		string? path = ExtractEditPath(request);
		if (path is null) {
			return null;
		}

		string before, after;
		lock (_gate) {
			if (!_current.TryGetValue(path, out string? current)) {
				return null;
			}
			after = current;
			before = _preEdit.GetValueOrDefault(path, string.Empty);
		}

		int? line = LineDiff.FirstChangedLine(before, after);
		return line is null ? null : $"{Relativize(path, request.Cwd)}:{line}";
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

	/// <summary>The files whose current content differs from their review baseline (the inline review diff set).</summary>
	public IReadOnlyList<FileChange> TurnChanges() {
		lock (_gate) {
			var changes = new List<FileChange>();
			foreach (var (path, baseline) in _reviewBaseline) {
				if (_current.TryGetValue(path, out string? current) && !string.Equals(baseline, current, StringComparison.Ordinal)) {
					changes.Add(new FileChange { Path = path, BaselineText = baseline, CurrentText = current });
				}
			}

			return changes;
		}
	}

	/// <summary>
	/// The change for <paramref name="path"/> against its review baseline, or <see langword="null"/> if the file
	/// isn't tracked. Baseline may equal current (e.g. just accepted/reverted) — the caller treats an equal pair
	/// as "no markers".
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	public FileChange? GetTurn(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			if (!_reviewBaseline.TryGetValue(path, out string? baseline) || !_current.TryGetValue(path, out string? current)) {
				return null;
			}

			return new FileChange { Path = path, BaselineText = baseline, CurrentText = current };
		}
	}

	private string ReadOrEmpty(string path) => _fileSystem.FileExists(path) ? _fileSystem.ReadAllText(path) : string.Empty;

	// Split text the way a Monaco model does (CRLF/CR normalized to LF, split on LF), so the web's line ranges
	// and guardText (built from the model's getLinesContent()) line up with Core's slices. Joining the result
	// back with "\n" round-trips the content (a trailing newline becomes a trailing empty element).
	private static List<string> SplitLines(string text) =>
		[.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n')];

	// A 1-based, end-exclusive line range into `lines` (a doc of N lines has valid ranges within [1, N+1)).
	// Returns the sliced lines; false (with an empty slice) when the range is out of bounds — treated by the
	// caller as a guard failure so an inconsistent request never writes.
	private static bool TryGetSlice(List<string> lines, LineRange range, out List<string> slice) {
		slice = [];
		if (range.Start < 1 || range.EndExclusive < range.Start || range.EndExclusive - 1 > lines.Count) {
			return false;
		}

		slice = lines.GetRange(range.Start - 1, range.EndExclusive - range.Start);
		return true;
	}

	private static string? ExtractEditPath(HookRequest request) {
		string? key = request.ToolName switch {
			"Edit" or "Write" or "MultiEdit" => "file_path",
			"NotebookEdit" => "notebook_path",
			_ => null,
		};
		if (key is null) {
			return null;
		}

		try {
			using var doc = JsonDocument.Parse(request.ToolInputJson);
			if (doc.RootElement.ValueKind != JsonValueKind.Object
				|| !doc.RootElement.TryGetProperty(key, out var value)
				|| value.ValueKind != JsonValueKind.String) {
				return null;
			}

			string raw = value.GetString() ?? string.Empty;
			return string.IsNullOrEmpty(raw) ? null : Resolve(raw, request.Cwd);
		} catch (JsonException) {
			return null;
		}
	}

	private static string Resolve(string path, string? cwd) =>
		Path.IsPathRooted(path) || string.IsNullOrEmpty(cwd) ? path : Path.GetFullPath(path, cwd);

	// Render an absolute path relative to cwd (the workspace), with '/' separators so the terminal's
	// file:line link detection (forward-slash only) catches it on Windows too. Falls back to the absolute
	// path when the file sits outside cwd (a "../" escape) or cwd is unknown.
	private static string Relativize(string absolutePath, string? cwd) {
		if (string.IsNullOrEmpty(cwd)) {
			return absolutePath.Replace('\\', '/');
		}

		string relative = Path.GetRelativePath(cwd, absolutePath);
		bool escapes = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative);
		return (escapes ? absolutePath : relative).Replace('\\', '/');
	}
}
