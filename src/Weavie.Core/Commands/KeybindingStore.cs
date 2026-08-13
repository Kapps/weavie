using System.Text.Json;
using Weavie.Core.FileSystem;

namespace Weavie.Core.Commands;

/// <summary>
/// Loads the user keybindings from <c>~/.weavie/keybindings.json</c>, merges them over the command defaults,
/// and is the change hub the host re-pushes from. The file is a JSON array of <c>{ "key", "command", "args"?,
/// "when"? }</c> records; a <c>"command": "-&lt;id&gt;"</c> entry unbinds a default (VS Code syntax).
/// Read-only from Core's side, watched through the shared validated-file projection so a half-typed save
/// never thrashes reactions and a malformed file keeps the last-good list (logged loudly).
/// See <c>docs/specs/commands.md</c>.
/// </summary>
public sealed class KeybindingStore : IDisposable {
	private readonly CommandRegistry _registry;
	private readonly Lock _gate = new();
	private readonly ReloadingFile<UserFile> _file;

	private List<ResolvedKeybinding> _resolved = [];
	private IReadOnlyList<string> _unknownCommands = [];
	private string _resolvedJson = "[]";

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

		_file = new ReloadingFile<UserFile>(FilePath, _gate, UserFile.Empty, LoadUserFile, watch: false);
		lock (_gate) {
			ApplyUserFileLocked(_file.Value);
		}
		_file.Reloaded += OnFileReloaded;
		if (enableWatcher) {
			_file.Watch();
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
	public bool IsMalformed => _file.Error is not null;

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
	public void Dispose() => _file.Dispose();

	private void OnFileReloaded(FileReload<UserFile> reload) {
		bool changed;
		bool unknownChanged;
		bool malformedChanged = (reload.PreviousError is null) != (reload.Error is null);
		IReadOnlyList<string> unknown;
		lock (_gate) {
			(changed, unknownChanged) = reload.Error is null
				? ApplyUserFileLocked(reload.Value)
				: (false, false);
			unknown = _unknownCommands;
		}
		if (reload.Error is not null) {
			Log?.Invoke($"[keybindings] {FilePath} is invalid ({reload.Error.Message}); keeping the last-good bindings until fixed.");
		}

		if (changed) {
			KeybindingsChanged?.Invoke();
		}

		if (unknownChanged) {
			UnknownCommandsChanged?.Invoke(unknown);
		}

		if (malformedChanged) {
			MalformedChanged?.Invoke(reload.Error is not null);
		}
	}

	private (bool ResolvedChanged, bool UnknownChanged) ApplyUserFileLocked(UserFile file) {
		var merged = MergeLocked(file.Entries);
		bool unknownChanged = !file.UnknownCommands.SequenceEqual(_unknownCommands, StringComparer.Ordinal);
		_unknownCommands = file.UnknownCommands;
		string json = CommandCatalog.BuildKeybindingsArrayJson(merged);
		if (string.Equals(json, _resolvedJson, StringComparison.Ordinal)) {
			return (false, unknownChanged);
		}

		_resolved = merged;
		_resolvedJson = json;
		return (true, unknownChanged);
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
					ActiveInModal = definition.KeybindingsActiveInModal,
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
				var definition = _registry.Require(entry.Command);
				result.Add(new ResolvedKeybinding {
					Key = entry.Key,
					Command = entry.Command,
					ArgsJson = entry.ArgsJson,
					When = entry.When,
					ActiveInModal = definition.KeybindingsActiveInModal,
					Global = entry.Global,
				});
			}
		}

		return result;
	}

	private UserFile LoadUserFile(string path) {
		string text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;

		if (string.IsNullOrWhiteSpace(text)) {
			return UserFile.Empty;
		}

		JsonDocument doc;
		try {
			doc = JsonDocument.Parse(text, new JsonDocumentOptions {
				CommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true,
			});
		} catch (JsonException ex) {
			throw new InvalidDataException($"JSON parse error: {ex.Message}", ex);
		}

		using (doc) {
			if (doc.RootElement.ValueKind != JsonValueKind.Array) {
				throw new InvalidDataException("Expected a JSON array of bindings.");
			}

			var entries = new List<UserBinding>();
			var unknownCommands = new List<string>();
			foreach (var element in doc.RootElement.EnumerateArray()) {
				if (TryParseEntry(element, unknownCommands, out var entry)) {
					entries.Add(entry);
				}
			}

			return new UserFile(entries, unknownCommands);
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

	private sealed record UserFile(IReadOnlyList<UserBinding> Entries, IReadOnlyList<string> UnknownCommands) {
		internal static UserFile Empty { get; } = new([], []);
	}
}
