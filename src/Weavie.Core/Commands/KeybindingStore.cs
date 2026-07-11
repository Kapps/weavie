using System.Text.Json;

namespace Weavie.Core.Commands;

/// <summary>
/// Loads the user keybindings from <c>~/.weavie/keybindings.json</c>, merges them over the command defaults,
/// and is the change hub the host re-pushes from. The file is a JSON array of <c>{ "key", "command", "args"?,
/// "when"? }</c> records; a <c>"command": "-&lt;id&gt;"</c> entry unbinds a default (VS Code syntax).
/// Read-only from Core's side, watched via a debounced, parse-guarded <see cref="FileSystemWatcher"/> so a
/// half-typed save never thrashes reactions and a malformed file keeps the last-good list (logged loudly).
/// See <c>docs/specs/commands.md</c>.
/// </summary>
public sealed class KeybindingStore : IDisposable {
	private readonly CommandRegistry _registry;
	private readonly Lock _gate = new();
	private readonly FileSystemWatcher? _watcher;
	private readonly Timer? _debounce;

	private List<ResolvedKeybinding> _resolved = [];
	private IReadOnlyList<string> _unknownCommands = [];
	private string _resolvedJson = "[]";
	// Whether the file currently has JSON parse errors (its bindings ignored), and whether we've ever resolved a
	// good file — the first-ever load of a malformed file has no last-good to keep, so it falls back to defaults.
	private bool _malformed;
	private bool _hasResolvedOnce;
	private bool _disposed;

	/// <summary>
	/// Creates a store over <paramref name="filePath"/> (default <c>~/.weavie/keybindings.json</c>), loading +
	/// merging now and — unless <paramref name="enableWatcher"/> is false — watching the file for external edits.
	/// </summary>
	public KeybindingStore(CommandRegistry registry, string? filePath, bool enableWatcher) {
		ArgumentNullException.ThrowIfNull(registry);
		_registry = registry;
		FilePath = filePath ?? WeaviePaths.KeybindingsFile;

		string? directory = Path.GetDirectoryName(FilePath);
		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		lock (_gate) {
			ReloadLocked();
		}

		if (enableWatcher && !string.IsNullOrEmpty(directory)) {
			_debounce = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
			_watcher = new FileSystemWatcher(directory, Path.GetFileName(FilePath)) {
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
				EnableRaisingEvents = true,
			};
			_watcher.Changed += OnFileEvent;
			_watcher.Created += OnFileEvent;
			_watcher.Renamed += OnFileEvent;
			_watcher.Deleted += OnFileEvent;
		}
	}

	/// <summary>Raised (off the UI thread) when the resolved bindings change after a file edit.</summary>
	public event Action? KeybindingsChanged;

	/// <summary>Diagnostic log line: parse errors and dropped (unknown-command) entries.</summary>
	public event Action<string>? Log;

	/// <summary>Raised (off the UI thread) when the set of unknown command ids in the user file changes on a
	/// live edit — so a host can surface (or clear) a "binding for unknown command…" warning.</summary>
	public event Action<IReadOnlyList<string>>? UnknownCommandsChanged;

	/// <summary>Raised when the file's malformed (JSON parse error) state flips on a live edit — true once it
	/// has parse errors (its bindings ignored, the last-good list kept), false once it parses cleanly again.</summary>
	public event Action<bool>? MalformedChanged;

	/// <summary>Whether the user file currently has JSON parse errors, so its bindings are being ignored.</summary>
	public bool IsMalformed {
		get { lock (_gate) { return _malformed; } }
	}

	/// <summary>The command ids in the user file that match no registered command (their bindings are dropped);
	/// empty when the file is clean.</summary>
	public IReadOnlyList<string> UnknownCommands {
		get { lock (_gate) { return _unknownCommands.ToArray(); } }
	}

	/// <summary>The keybindings file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>A snapshot of the current resolved bindings.</summary>
	public IReadOnlyList<ResolvedKeybinding> Resolved {
		get { lock (_gate) { return _resolved.ToArray(); } }
	}

	/// <summary>The resolved keybindings as a JSON array (for <c>__WEAVIE_KEYBINDINGS__</c> injection + the push message).</summary>
	public string BuildKeybindingsJson() {
		lock (_gate) {
			return _resolvedJson;
		}
	}

	/// <summary>The full command catalog (with current keys) as a JSON array (for <c>__WEAVIE_COMMANDS__</c> + <c>listCommands</c>).</summary>
	public string BuildCommandsJson() {
		lock (_gate) {
			return CommandCatalog.BuildCommandsArrayJson(_registry.Definitions, _resolved);
		}
	}

	/// <inheritdoc/>
	public void Dispose() {
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_disposed = true;
		}

		if (_watcher is not null) {
			_watcher.EnableRaisingEvents = false;
			_watcher.Changed -= OnFileEvent;
			_watcher.Created -= OnFileEvent;
			_watcher.Renamed -= OnFileEvent;
			_watcher.Deleted -= OnFileEvent;
			_watcher.Dispose();
		}

		_debounce?.Dispose();
	}

	// Under _gate: a watcher callback in flight during Dispose must not touch the disposed timer.
	private void OnFileEvent(object sender, FileSystemEventArgs e) {
		lock (_gate) {
			if (!_disposed) {
				_debounce?.Change(250, Timeout.Infinite);
			}
		}
	}

	private void OnDebounceElapsed(object? state) {
		bool changed;
		bool unknownChanged;
		bool malformedChanged;
		bool malformed;
		IReadOnlyList<string> unknown;
		lock (_gate) {
			if (_disposed) {
				return;
			}

			(changed, unknownChanged, malformedChanged) = ReloadLocked();
			unknown = _unknownCommands;
			malformed = _malformed;
		}

		if (changed) {
			KeybindingsChanged?.Invoke();
		}

		if (unknownChanged) {
			UnknownCommandsChanged?.Invoke(unknown);
		}

		if (malformedChanged) {
			MalformedChanged?.Invoke(malformed);
		}
	}

	// Reads + merges the file, swapping in the new resolved list. Returns whether the resolved JSON actually
	// changed, so the watch path only fires on a real change. A malformed file keeps the last-good list.
	private (bool ResolvedChanged, bool UnknownChanged, bool MalformedChanged) ReloadLocked() {
		bool wasMalformed = _malformed;
		var unknown = new List<string>();
		var entries = ReadUserEntriesLocked(unknown); // sets _malformed
		bool malformedChanged = _malformed != wasMalformed;

		// A malformed file with a prior good resolution keeps the last-good list (and its unknown set): a
		// transient parse error must not wipe the user's customizations. The first-ever load has no last-good,
		// so it falls through to build defaults (entries is empty there).
		if (_malformed && _hasResolvedOnce) {
			return (false, false, malformedChanged);
		}

		var merged = MergeLocked(entries);
		bool unknownChanged = !unknown.SequenceEqual(_unknownCommands, StringComparer.Ordinal);
		_unknownCommands = unknown;
		_hasResolvedOnce = true;
		string json = CommandCatalog.BuildKeybindingsArrayJson(merged);
		if (string.Equals(json, _resolvedJson, StringComparison.Ordinal)) {
			return (false, unknownChanged, malformedChanged);
		}

		_resolved = merged;
		_resolvedJson = json;
		return (true, unknownChanged, malformedChanged);
	}

	private List<ResolvedKeybinding> MergeLocked(IReadOnlyList<UserBinding> userEntries) {
		var result = new List<ResolvedKeybinding>();
		foreach (var definition in _registry.Definitions) {
			foreach (var binding in definition.DefaultKeybindings) {
				result.Add(new ResolvedKeybinding {
					Key = binding.Key,
					Command = definition.Id,
					ArgsJson = binding.ArgsJson,
					// A per-binding guard overrides the command-level one (and never gates palette visibility),
					// so one chord can be focus-scoped while the command stays in the palette.
					When = binding.When ?? definition.When,
					Global = binding.Global,
				});
			}
		}

		// Apply user entries in order: unbind removes a matching (key, command); a normal entry adds/overrides
		// (the web resolves last-match-first, so later entries win for the same key).
		foreach (var entry in userEntries) {
			if (entry.IsUnbind) {
				result.RemoveAll(b =>
					string.Equals(b.Key, entry.Key, StringComparison.Ordinal)
					&& string.Equals(b.Command, entry.Command, StringComparison.Ordinal));
			} else {
				result.Add(new ResolvedKeybinding {
					Key = entry.Key,
					Command = entry.Command,
					ArgsJson = entry.ArgsJson,
					When = entry.When,
					Global = entry.Global,
				});
			}
		}

		return result;
	}

	private IReadOnlyList<UserBinding> ReadUserEntriesLocked(List<string> unknownCommands) {
		_malformed = false;
		string text;
		try {
			text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[keybindings] could not read {FilePath}: {ex.Message}; using defaults.");
			return [];
		}

		if (string.IsNullOrWhiteSpace(text)) {
			return [];
		}

		JsonDocument doc;
		try {
			doc = JsonDocument.Parse(text, new JsonDocumentOptions {
				CommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true,
			});
		} catch (JsonException ex) {
			_malformed = true;
			Log?.Invoke($"[keybindings] {FilePath} has JSON parse errors ({ex.Message}); keeping the last-good bindings until fixed.");
			return [];
		}

		using (doc) {
			if (doc.RootElement.ValueKind != JsonValueKind.Array) {
				_malformed = true;
				Log?.Invoke($"[keybindings] {FilePath} must be a JSON array of bindings; keeping the last-good bindings until fixed.");
				return [];
			}

			var entries = new List<UserBinding>();
			foreach (var element in doc.RootElement.EnumerateArray()) {
				if (TryParseEntry(element, unknownCommands, out var entry)) {
					entries.Add(entry);
				}
			}

			return entries;
		}
	}

	private bool TryParseEntry(JsonElement element, List<string> unknownCommands, out UserBinding entry) {
		entry = default;
		if (element.ValueKind != JsonValueKind.Object) {
			Log?.Invoke("[keybindings] skipping a non-object entry.");
			return false;
		}

		string? key = element.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
		string? command = element.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
		if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(command)) {
			Log?.Invoke("[keybindings] skipping an entry missing 'key' or 'command'.");
			return false;
		}

		bool isUnbind = command.StartsWith('-');
		string targetId = isUnbind ? command[1..] : command;
		if (!_registry.TryGet(targetId, out _)) {
			Log?.Invoke($"[keybindings] dropping binding for unknown command '{targetId}'.");
			unknownCommands.Add(targetId);
			return false;
		}

		string? argsJson = element.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Object
			? a.GetRawText()
			: null;
		string? when = element.TryGetProperty("when", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
		bool global = element.TryGetProperty("global", out var g) && g.ValueKind == JsonValueKind.True;

		entry = new UserBinding(key, targetId, argsJson, when, global, isUnbind);
		return true;
	}

	private readonly record struct UserBinding(string Key, string Command, string? ArgsJson, string? When, bool Global, bool IsUnbind);
}
