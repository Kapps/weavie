using System.Text.Json;
using Weavie.Core.FileSystem;
using Weavie.Core.Hooks;

namespace Weavie.Core.Changes;

/// <summary>
/// Records every file changed during the session and the diff against each file's session baseline. Fed by the
/// hook stream via <see cref="Observe"/>, which fires in every permission mode (the hook runs before the
/// permission check). Read on the host UI thread but mutated from the hook accept loop — hence the lock.
/// <para>
/// Scoped by <c>isInScope</c> to the session's worktree (+ scratch): an out-of-scope edit is dropped. The
/// tracker's scope must match the editor's <c>file://</c> provider scope, else an open is pushed for a path
/// the editor can't read, stranding a blank tab.
/// </para>
/// </summary>
public sealed class SessionChangeTracker {
	private readonly IFileSystem _fileSystem;
	private readonly Func<string, bool> _isInScope;
	private readonly object _gate = new();
	private readonly Dictionary<string, string> _baseline = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _current = new(StringComparer.Ordinal);
	// Each file's last-reviewed content; advanced only on keep-all (AcceptTurn) or a per-hunk revert, not on a
	// turn boundary, so the review set accumulates everything unacknowledged across turns (docs/specs/turn-review.md).
	private readonly Dictionary<string, string> _reviewBaseline = new(StringComparer.Ordinal);
	// Each file's content at the most recent edit's PreToolUse; diffed against post-edit in EditLocationFor.
	private readonly Dictionary<string, string> _preEdit = new(StringComparer.Ordinal);
	// Files absent on disk when their review baseline was captured, so reverting their last hunk deletes rather
	// than leaves a 0-byte file. Keys off existence-at-baseline, not emptiness.
	private readonly HashSet<string> _createdSinceBaseline = new(StringComparer.Ordinal);

	/// <summary>Creates a tracker that reads file content through <paramref name="fileSystem"/>.</summary>
	/// <param name="fileSystem">The session filesystem the tracker reads changed-file content through.</param>
	/// <param name="isInScope">
	/// Predicate over an absolute path: only edits it accepts are tracked. See the type remarks.
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
	/// Raised with the absolute path of a tracked file that disappeared from disk (a <c>Bash</c> rm/rename/temp
	/// cleanup), so the host can close its editor tab. Fires from <see cref="Observe"/>'s post-tool reconciliation.
	/// </summary>
	public event Action<string>? FileDeleted;

	/// <summary>
	/// Folds a hook event into the change set: PreToolUse snapshots the baseline, PostToolUse records the new
	/// content and reconciles disk deletions (<see cref="ReconcileDeletions"/>) so a mid-turn rm leaves the review
	/// set. Out-of-scope edits are dropped; turn boundaries are ignored so the review baseline never resets.
	/// </summary>
	public void Observe(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);

		// Reconcile deletions before recording this tool's edit, so a Bash rm/mv drops the vanished file first.
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
	/// Keep-all: advances every review baseline to current content, clearing the inline diff. The session diff
	/// (vs the session baseline) is kept, and nothing counts as "created since baseline" any more.
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
			// Disk content here is the pre-edit file. Seed baselines if missing; track existence so a later revert
			// deletes rather than truncates a file that didn't yet exist.
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
			// First review-touch with no prior CaptureBaseline: the file appeared this session, so it didn't exist at baseline.
			if (_reviewBaseline.TryAdd(path, string.Empty)) {
				_createdSinceBaseline.Add(path);
			}

			_current[path] = ReadOrEmpty(path);
		}

		Changed?.Invoke();
		FileChanged?.Invoke(path);
	}

	/// <summary>
	/// Drops any recorded file that no longer exists on disk (deleted out from under us), since there's nothing to
	/// render. Raises <see cref="FileDeleted"/> per removed path and a single <see cref="Changed"/>; a no-op when
	/// nothing vanished.
	/// </summary>
	private void ReconcileDeletions() {
		List<string>? removed = null;
		lock (_gate) {
			// Snapshot keys first: Forget mutates _current while we iterate. Only _current files appear in the review walk.
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

			// Reverting the last hunk of a created file returns it to non-existence — delete and forget it.
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
	/// Reverts a whole file to its review baseline (per-file undo). A created-since-baseline file is deleted, not
	/// truncated. No guard — the whole file is reset, not a single hunk against concurrent edits.
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
			var currentLines = SplitLines(ReadOrEmpty(path));
			if (!TryGetSlice(currentLines, currentRange, out var currentSlice)
			|| !string.Equals(string.Join("\n", currentSlice), guardText, StringComparison.Ordinal)) {
				return false;
			}

			var baselineLines = SplitLines(_reviewBaseline.GetValueOrDefault(path, string.Empty));
			if (!TryGetSlice(baselineLines, baselineRange, out _)) {
				return false;
			}

			baselineLines.RemoveRange(baselineRange.Start - 1, baselineRange.EndExclusive - baselineRange.Start);
			baselineLines.InsertRange(baselineRange.Start - 1, currentSlice);
			_reviewBaseline[path] = string.Join("\n", baselineLines);
			_current[path] = string.Join("\n", currentLines);
			return true;
		}
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

			string current = ReadOrEmpty(path);
			_reviewBaseline[path] = current;
			_current[path] = current;
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
	/// A workspace-relative <c>path:line</c> jump target for the first line a just-recorded edit changed, with
	/// <c>/</c> separators so it's clickable on every platform. <see langword="null"/> for non-edit/non-PostToolUse
	/// events, notebooks, and no-op edits. Call after <see cref="Observe"/> folds in the PostToolUse event.
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

	// Split text the way a Monaco model does (CRLF/CR normalized to LF), so the web's line ranges line up with Core's slices.
	private static List<string> SplitLines(string text) =>
		[.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n')];

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

	// Path relative to cwd with '/' separators so the terminal's (forward-slash-only) file:line link detection
	// catches it on Windows too. Falls back to the absolute path on a "../" escape or unknown cwd.
	private static string Relativize(string absolutePath, string? cwd) {
		if (string.IsNullOrEmpty(cwd)) {
			return absolutePath.Replace('\\', '/');
		}

		string relative = Path.GetRelativePath(cwd, absolutePath);
		bool escapes = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative);
		return (escapes ? absolutePath : relative).Replace('\\', '/');
	}
}
