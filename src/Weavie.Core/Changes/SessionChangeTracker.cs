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

	/// <summary>Creates a tracker that reads file content through <paramref name="fileSystem"/>.</summary>
	/// <param name="fileSystem">The filesystem seam used to snapshot baseline + current content.</param>
	public SessionChangeTracker(IFileSystem fileSystem) {
		ArgumentNullException.ThrowIfNull(fileSystem);
		_fileSystem = fileSystem;
	}

	/// <summary>Raised whenever the change set updates (a file's current content was recorded).</summary>
	public event Action? Changed;

	/// <summary>
	/// Folds a hook event into the change set: PreToolUse on a file-editing tool snapshots the baseline,
	/// PostToolUse records the new content. Non-editing tools (Bash, etc.) are ignored.
	/// </summary>
	/// <param name="request">The observed hook event.</param>
	public void Observe(HookRequest request) {
		ArgumentNullException.ThrowIfNull(request);
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

	/// <summary>Snapshots <paramref name="path"/>'s current content as its session baseline, once.</summary>
	/// <param name="path">Absolute file path.</param>
	public void CaptureBaseline(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			if (!_baseline.ContainsKey(path)) {
				_baseline[path] = ReadOrEmpty(path);
			}
		}
	}

	/// <summary>Records <paramref name="path"/>'s latest content (baselining to empty if it appeared this session).</summary>
	/// <param name="path">Absolute file path.</param>
	public void RecordChange(string path) {
		ArgumentException.ThrowIfNullOrEmpty(path);
		lock (_gate) {
			_baseline.TryAdd(path, string.Empty);
			_current[path] = ReadOrEmpty(path);
		}
		Changed?.Invoke();
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
