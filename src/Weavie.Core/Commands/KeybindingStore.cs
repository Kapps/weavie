using System.Text.Json;

namespace Weavie.Core.Commands;

/// <summary>
/// Loads the user keybindings from <c>~/.weavie/keybindings.json</c>, merges them over the command defaults,
/// and is the change hub the host re-pushes from. The file is a JSON array of
/// <c>{ "key", "command", "args"?, "when"? }</c> records; a <c>"command": "-&lt;id&gt;"</c> entry unbinds a
/// default (VS Code's syntax). The merged list is shipped to the web and feeds <c>listCommands</c>.
///
/// Read-only from Core's side: the only writer is the user editing the file, watched via a debounced,
/// parse-guarded <see cref="FileSystemWatcher"/> so a half-typed save never thrashes reactions and a
/// malformed file keeps the last-good resolved list (logged loudly). See <c>docs/specs/commands.md</c>.
/// </summary>
public sealed class KeybindingStore : IDisposable {
	private readonly CommandRegistry _registry;
	private readonly Lock _gate = new();
	private readonly FileSystemWatcher? _watcher;
	private readonly Timer? _debounce;

	private List<ResolvedKeybinding> _resolved = [];
	private string _resolvedJson = "[]";
	private bool _disposed;

	/// <summary>
	/// Creates a store over <paramref name="filePath"/> (default <c>~/.weavie/keybindings.json</c>), loading +
	/// merging now and — unless <paramref name="enableWatcher"/> is false — watching the file for external
	/// edits. The parent directory is created so the watcher can attach.
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

	private void OnFileEvent(object sender, FileSystemEventArgs e) =>
		_debounce?.Change(250, Timeout.Infinite);

	private void OnDebounceElapsed(object? state) {
		bool changed;
		lock (_gate) {
			if (_disposed) {
				return;
			}

			changed = ReloadLocked();
		}

		if (changed) {
			KeybindingsChanged?.Invoke();
		}
	}

	// Reads + merges the file, swapping in the new resolved list. Returns whether the resolved JSON actually
	// changed, so the watch path only fires on a real change. A malformed file keeps the last-good list.
	private bool ReloadLocked() {
		var merged = MergeLocked(ReadUserEntriesLocked());
		string json = CommandCatalog.BuildKeybindingsArrayJson(merged);
		if (string.Equals(json, _resolvedJson, StringComparison.Ordinal)) {
			return false;
		}

		_resolved = merged;
		_resolvedJson = json;
		return true;
	}

	private List<ResolvedKeybinding> MergeLocked(IReadOnlyList<UserBinding> userEntries) {
		// Seed with the command defaults.
		var result = new List<ResolvedKeybinding>();
		foreach (var definition in _registry.Definitions) {
			foreach (var binding in definition.DefaultKeybindings) {
				result.Add(new ResolvedKeybinding {
					Key = binding.Key,
					Command = definition.Id,
					ArgsJson = binding.ArgsJson,
					When = definition.When,
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

	private IReadOnlyList<UserBinding> ReadUserEntriesLocked() {
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
			Log?.Invoke($"[keybindings] {FilePath} has JSON parse errors ({ex.Message}); using defaults until fixed.");
			return [];
		}

		using (doc) {
			if (doc.RootElement.ValueKind != JsonValueKind.Array) {
				Log?.Invoke($"[keybindings] {FilePath} must be a JSON array of bindings; using defaults.");
				return [];
			}

			var entries = new List<UserBinding>();
			foreach (var element in doc.RootElement.EnumerateArray()) {
				if (TryParseEntry(element, out var entry)) {
					entries.Add(entry);
				}
			}

			return entries;
		}
	}

	private bool TryParseEntry(JsonElement element, out UserBinding entry) {
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
