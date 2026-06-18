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
	// Per-TURN baseline: each file's content at the start of the current turn. Reset on UserPromptSubmit
	// (the prior turn is implicitly accepted). The inline diff renders against this, not the session baseline.
	private readonly Dictionary<string, string> _turnBaseline = new(StringComparer.Ordinal);

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

	/// <summary>Raised when a new turn starts (UserPromptSubmit), so the host can clear the inline turn markers.</summary>
	public event Action? TurnBegan;

	/// <summary>
	/// Folds a hook event into the change set: PreToolUse on a file-editing tool snapshots the baseline,
	/// PostToolUse records the new content. Non-editing tools (Bash, etc.) are ignored.
	/// </summary>
	/// <param name="request">The observed hook event.</param>
	public void Observe(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);
		if (request.Event == HookEventKind.UserPromptSubmit) {
			BeginTurn();
			return;
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
	/// Marks a new turn: the prior turn's changes are implicitly accepted, so the per-turn baseline is reset
	/// to the current content (turn diff becomes empty until the new turn edits). Raises <see cref="TurnBegan"/>.
	/// </summary>
	public void BeginTurn() {
		lock (_gate) {
			SnapshotTurnBaselineLocked();
		}

		TurnBegan?.Invoke();
	}

	/// <summary>Accepts the current turn's changes (resets the per-turn baseline to current), clearing the turn diff.</summary>
	public void AcceptTurn() {
		lock (_gate) {
			SnapshotTurnBaselineLocked();
		}
	}

	/// <summary>Snapshots <paramref name="path"/>'s current content as its session baseline, once.</summary>
	/// <param name="path">Absolute file path.</param>
	public void CaptureBaseline(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			// Read once; seed both the session baseline (first touch ever) and the turn baseline (first touch
			// this turn) if either is missing. Disk content here = the file before this edit = the right baseline.
			if (!_baseline.ContainsKey(path) || !_turnBaseline.ContainsKey(path)) {
				string content = ReadOrEmpty(path);
				_baseline.TryAdd(path, content);
				_turnBaseline.TryAdd(path, content);
			}
		}
	}

	/// <summary>Records <paramref name="path"/>'s latest content (baselining to empty if it appeared this session).</summary>
	/// <param name="path">Absolute file path.</param>
	public void RecordChange(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			_baseline.TryAdd(path, string.Empty);
			_turnBaseline.TryAdd(path, string.Empty);
			_current[path] = ReadOrEmpty(path);
		}
		Changed?.Invoke();
		FileChanged?.Invoke(path);
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

	/// <summary>The change set as compact per-file add/removed counts (for the changes list).</summary>
	public IReadOnlyList<ChangeSummary> Summarize() {
		var summaries = new List<ChangeSummary>();
		foreach (var change in Changes()) {
			var (added, removed) = LineDiff.Count(change.BaselineText, change.CurrentText);
			summaries.Add(new ChangeSummary { Path = change.Path, Added = added, Removed = removed });
		}
		return summaries;
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

	/// <summary>The files whose current content differs from this turn's baseline (the inline turn diff set).</summary>
	public IReadOnlyList<FileChange> TurnChanges() {
		lock (_gate) {
			var changes = new List<FileChange>();
			foreach (var (path, baseline) in _turnBaseline) {
				if (_current.TryGetValue(path, out string? current) && !string.Equals(baseline, current, StringComparison.Ordinal)) {
					changes.Add(new FileChange { Path = path, BaselineText = baseline, CurrentText = current });
				}
			}

			return changes;
		}
	}

	/// <summary>
	/// The change for <paramref name="path"/> against this turn's baseline, or <see langword="null"/> if the
	/// file wasn't touched this turn. Baseline may equal current (e.g. just accepted/reverted) — the caller
	/// treats an equal pair as "no markers".
	/// </summary>
	/// <param name="path">Absolute file path.</param>
	public FileChange? GetTurn(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			if (!_turnBaseline.TryGetValue(path, out string? baseline) || !_current.TryGetValue(path, out string? current)) {
				return null;
			}

			return new FileChange { Path = path, BaselineText = baseline, CurrentText = current };
		}
	}

	// Reset the per-turn baseline to the current content of every tracked file. Caller holds _gate.
	private void SnapshotTurnBaselineLocked() {
		_turnBaseline.Clear();
		foreach (var (path, content) in _current) {
			_turnBaseline[path] = content;
		}
	}

	private string ReadOrEmpty(string path) => _fileSystem.FileExists(path) ? _fileSystem.ReadAllText(path) : string.Empty;

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
}
