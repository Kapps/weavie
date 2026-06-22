using System.Text.Json;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;

namespace Weavie.Core.Changes;

/// <summary>
/// Records every file changed during the session and the diff against each file's session baseline (its
/// content the first time a tool touched it). Fed by the hook stream via <see cref="Observe"/>, which fires
/// for edits in every permission mode (the hook runs before the permission check), so the change feed is
/// independent of openDiff and the mode. PreToolUse snapshots the baseline; PostToolUse records the new
/// content. Read on the host's UI thread but mutated from the hook accept loop — hence the lock.
/// <para>
/// Scoped by <c>isInScope</c> to the session's own worktree (+ scratch): an edit outside it — Claude
/// editing its own memory/config files under <c>~/.claude</c>, say — is dropped, never tracked. The review
/// feed drives the editor to open each changed file, and the editor's <c>file://</c> provider only serves
/// the session's roots, so tracking an out-of-scope path would push an open that can't be read and strand a
/// blank tab. Keeping the tracker's scope == the file provider's scope guarantees every tracked file opens.
/// </para>
/// </summary>
public sealed class SessionChangeTracker {
	private readonly IFileSystem _fileSystem;
	private readonly Func<string, bool> _isInScope;
	private readonly object _gate = new();
	private readonly Dictionary<string, string> _baseline = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _current = new(StringComparer.Ordinal);
	// Review baseline: each file's last-reviewed content. Seeded on first touch and advanced only on an explicit
	// keep-all (AcceptTurn) or a per-hunk revert, not on a turn boundary. The accumulate model
	// (docs/specs/turn-review.md): the review set is everything Claude changed that you haven't acknowledged,
	// persisting across turns, so the inline diff renders against this baseline (TurnChanges/GetTurn).
	private readonly Dictionary<string, string> _reviewBaseline = new(StringComparer.Ordinal);
	// Per-edit pre-state: each file's content captured at the PreToolUse of the most recent edit, overwritten
	// every edit. Diffed against the post-edit content in EditLocationFor to point at the line this edit changed.
	private readonly Dictionary<string, string> _preEdit = new(StringComparer.Ordinal);
	// Files that did not exist on disk when their review baseline was captured. Reverting the last hunk of such a
	// file returns it to non-existence, so the revert deletes the file rather than leaving a 0-byte one. Keys off
	// existence-at-baseline, not emptiness (a genuinely empty file that existed at baseline is kept).
	private readonly HashSet<string> _createdSinceBaseline = new(StringComparer.Ordinal);

	/// <summary>Creates a tracker that reads file content through <paramref name="fileSystem"/>.</summary>
	/// <param name="fileSystem">The session filesystem the tracker reads changed-file content through.</param>
	/// <param name="isInScope">
	/// Predicate over an absolute path: only edits it accepts are tracked. The host scopes this to the session's
	/// worktree (+ scratch) so edits to files the editor can't open — Claude touching its own config under
	/// <c>~/.claude</c>, say — never reach the review feed. See the type remarks.
	/// </param>
	public SessionChangeTracker(IFileSystem fileSystem, Func<string, bool> isInScope) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		ArgumentNullException.ThrowIfNull(isInScope);
		_fileSystem = fileSystem;
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
	/// Raised with the absolute path of a tracked file that has disappeared from disk — deleted by a tool that
	/// isn't an editor (a <c>Bash</c> rm, a rename, a generated-then-cleaned-up temp) — so the host can close its
	/// editor tab. Fires from <see cref="Observe"/>'s post-tool reconciliation, alongside a single <see cref="Changed"/>.
	/// </summary>
	public event Action<string>? FileDeleted;

	/// <summary>
	/// Folds a hook event into the change set: PreToolUse on a file-editing tool snapshots the baseline,
	/// PostToolUse records the new content. A non-editing tool (Bash, etc.) records no edit, but its PostToolUse
	/// still triggers a disk reconciliation (<see cref="ReconcileDeletions"/>) so a file removed mid-turn — e.g.
	/// a <c>Bash</c> rm of a scratch file — leaves the review set instead of stranding the ← / → walk on a missing
	/// path. An edit to a path outside the session's scope (see the constructor's <c>isInScope</c>) is dropped.
	/// Turn boundaries are ignored, so the review baseline never resets on a new prompt.
	/// </summary>
	public void Observe(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);

		// A PostToolUse settles a completed tool: reconcile FIRST so a deletion by any tool (the edit tools never
		// delete; a Bash rm / mv does) drops the vanished file before we record this tool's own edit (if any).
		// Reconcile touches only already-tracked files, so an out-of-scope edit can't have seeded one to clean up.
		if (request.Event == HookEventKind.PostToolUse) {
			ReconcileDeletions();
		}

		string? path = ExtractEditPath(request);
		if (path is null || !_isInScope(path)) {
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
	/// content, clearing the inline review diff. The session diff (vs the session baseline) is kept. Everything
	/// now sits at its baseline, so nothing counts as "created since baseline".
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
			// Disk content here is the file before this edit. Seed the session and review baselines if missing,
			// and always record the per-edit pre-state so EditLocationFor can pinpoint this edit's line. If the
			// file doesn't exist yet, remember that so a later revert deletes rather than truncates it.
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
			// First review-touch with no prior CaptureBaseline means the file appeared this session without a
			// pre-snapshot, so it didn't exist at baseline.
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
	/// content supplied over a message — the hook-bridge security rule). <paramref name="guardText"/> is an
	/// optimistic-concurrency check: if the file's current lines no longer match it (a parallel agent or a later
	/// edit moved the file), the revert aborts without writing (<see cref="RevertHunkOutcome.GuardMismatch"/>).
	/// On a match the hunk's current lines are replaced by the baseline lines; a created-since-baseline file whose
	/// revert empties it is deleted (<see cref="RevertHunkOutcome.Deleted"/>) rather than truncated to 0 bytes.
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

			// Reverting the last hunk of a created file returns it to non-existence — delete it and drop it from
			// tracking entirely.
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
	/// Reverts a whole file to its review baseline (the whole-set / per-file undo). A file created since its
	/// baseline is deleted rather than truncated to 0 bytes; any other file is rewritten to its baseline content.
	/// No guard — the whole file is being reset, not a single hunk against concurrent edits.
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

	// Drops a path from every tracked set after the file was deleted on revert. Caller holds _gate.
	private void Forget(string path) {
		_current.Remove(path);
		_baseline.Remove(path);
		_reviewBaseline.Remove(path);
		_preEdit.Remove(path);
		_createdSinceBaseline.Remove(path);
	}

	/// <summary>
	/// A workspace-relative <c>path:line</c> reference to the first line a just-recorded edit changed, as a
	/// clickable jump target. Call after <see cref="Observe"/> has folded in the PostToolUse event. Returns
	/// <see langword="null"/> for non-edit / non-PostToolUse events, for notebooks, and when the edit changed no
	/// line. Paths use <c>/</c> separators so the reference is clickable on every platform.
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
	/// isn't tracked. Baseline may equal current — the caller treats an equal pair as "no markers".
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
	// and guardText line up with Core's slices. Joining back with "\n" round-trips the content.
	private static List<string> SplitLines(string text) =>
		[.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n')];

	// Slices a 1-based, end-exclusive line range out of `lines`. Returns false (empty slice) when out of bounds —
	// the caller treats that as a guard failure so an inconsistent request never writes.
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

	// Render an absolute path relative to cwd with '/' separators so the terminal's file:line link detection
	// (forward-slash only) catches it on Windows too. Falls back to the absolute path on a "../" escape or unknown cwd.
	private static string Relativize(string absolutePath, string? cwd) {
		if (string.IsNullOrEmpty(cwd)) {
			return absolutePath.Replace('\\', '/');
		}

		string relative = Path.GetRelativePath(cwd, absolutePath);
		bool escapes = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative);
		return (escapes ? absolutePath : relative).Replace('\\', '/');
	}
}
