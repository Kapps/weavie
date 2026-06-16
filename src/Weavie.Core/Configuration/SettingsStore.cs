using System.Globalization;
using System.Text;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace Weavie.Core.Configuration;

/// <summary>A resolved setting value and the layer it came from.</summary>
public readonly record struct ResolvedValue(object? Value, SettingSource Source);

/// <summary>The outcome of a <see cref="SettingsStore.Set"/> — what to tell the user.</summary>
public sealed record SetResult {
	/// <summary>Whether the value was written to the user file (always true on success).</summary>
	public required bool Written { get; init; }

	/// <summary>The env var that overrides the file (so the effective value is unchanged), or <c>null</c>.</summary>
	public string? ShadowedByEnv { get; init; }

	/// <summary>How the change takes effect.</summary>
	public required ApplyMode Apply { get; init; }
}

/// <summary>A single resolved-value change, raised to subscribers of <see cref="SettingsStore.SettingChanged"/>.</summary>
public readonly record struct SettingChange(string Key, object? OldValue, object? NewValue, SettingSource Source);

/// <summary>
/// Loads, resolves, and persists user settings as TOML at <c>~/.weavie/settings.toml</c>, and is the
/// change hub the host reacts to. Resolution precedence is env var → user file → registered default;
/// values are coerced to their declared <see cref="SettingKind"/> and validated. Writes go through
/// Tomlyn's comment-preserving <see cref="DocumentSyntax"/> (unknown <c>[plugins.*]</c> subtrees and
/// user comments survive a round-trip) and are atomic. A debounced, parse-guarded
/// <see cref="FileSystemWatcher"/> turns hand-edits into the same <see cref="SettingChanged"/> events
/// that <see cref="Set"/> raises, diffing against the in-memory resolved state so self-writes never
/// double-fire. See <c>docs/specs/settings.md</c>.
/// </summary>
public sealed class SettingsStore : IDisposable {
	private const string WorkspaceKey = "workspace";

	private readonly SettingsRegistry _registry;
	private readonly Lock _gate = new();
	private readonly Dictionary<string, object?> _resolved = new(StringComparer.Ordinal);
	private readonly FileSystemWatcher? _watcher;
	private readonly Timer? _debounce;

	private DocumentSyntax _doc = new();
	private TomlTable _model = [];
	private bool _malformed;
	private bool _hasGoodDoc;
	private bool _disposed;

	/// <summary>
	/// Creates a store over <paramref name="filePath"/> (default <c>~/.weavie/settings.toml</c>),
	/// loading current values and—unless <paramref name="enableWatcher"/> is false—watching the file
	/// for external edits. The parent directory is created so the watcher can attach.
	/// </summary>
	public SettingsStore(SettingsRegistry registry, string? filePath = null, bool enableWatcher = true) {
		ArgumentNullException.ThrowIfNull(registry);
		_registry = registry;
		FilePath = filePath ?? WeaviePaths.SettingsFile;

		var directory = Path.GetDirectoryName(FilePath);
		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}

		lock (_gate) {
			LoadFromDiskLocked();
			foreach (var definition in _registry.Definitions) {
				_resolved[definition.Key] = ResolveLocked(definition).Value;
			}
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
		}
	}

	/// <summary>Raised (off the UI thread) once per resolved value that actually changed.</summary>
	public event Action<SettingChange>? SettingChanged;

	/// <summary>Diagnostic log line — loud surfacing of parse errors and ignored invalid values.</summary>
	public event Action<string>? Log;

	/// <summary>The settings file backing this store.</summary>
	public string FilePath { get; }

	/// <summary>Whether the on-disk file currently has TOML parse errors (writes are refused while true).</summary>
	public bool IsMalformed {
		get { lock (_gate) { return _malformed; } }
	}

	/// <summary>Resolves <paramref name="key"/> through env → file → default and reports the source.</summary>
	public ResolvedValue Resolve(string key) {
		lock (_gate) {
			return ResolveLocked(_registry.Require(key));
		}
	}

	/// <summary>Resolves <paramref name="key"/> as a string (null if the value is absent or not a string).</summary>
	public string? GetString(string key) => Resolve(key).Value as string;

	/// <summary>
	/// Validates and writes <paramref name="key"/> = <paramref name="value"/> (a JSON value from MCP)
	/// to the user file, raising <see cref="SettingChanged"/> if the effective value changed. Throws
	/// <see cref="UnknownSettingException"/>, <see cref="SettingValidationException"/>, or
	/// <see cref="SettingsFileMalformedException"/> on rejection.
	/// </summary>
	public SetResult Set(string key, JsonElement value) {
		List<SettingChange> changes;
		SetResult result;
		lock (_gate) {
			var definition = _registry.Require(key);
			if (_malformed) {
				throw new SettingsFileMalformedException(
					$"{FilePath} has TOML parse errors; fix or delete it before changing settings.");
			}

			if (!TryCoerceJson(definition, value, out var coerced, out var coerceError)) {
				throw new SettingValidationException(key, coerceError!);
			}

			var validation = ValidateValue(definition, coerced);
			if (!validation.IsValid) {
				throw new SettingValidationException(key, validation.Message ?? "invalid value");
			}

			SetDocValueLocked(definition, coerced);
			SaveAtomicLocked();
			changes = RecomputeAndDiffLocked();

			var shadow = ResolveLocked(definition).Source == SettingSource.Environment ? definition.EnvVar : null;
			result = new SetResult { Written = true, ShadowedByEnv = shadow, Apply = definition.Apply };
		}

		RaiseChanges(changes);
		return result;
	}

	/// <summary>Subscribes <paramref name="handler"/> to changes of a single <paramref name="key"/>.</summary>
	public IDisposable Subscribe(string key, Action<SettingChange> handler) {
		ArgumentNullException.ThrowIfNull(handler);
		void Filtered(SettingChange change) {
			if (string.Equals(change.Key, key, StringComparison.Ordinal)) {
				handler(change);
			}
		}

		SettingChanged += Filtered;
		return new Subscription(() => SettingChanged -= Filtered);
	}

	/// <summary>Builds the <c>listSettings</c> catalog JSON: every setting with value, source, default, and metadata.</summary>
	public string BuildCatalogJson() {
		lock (_gate) {
			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream)) {
				writer.WriteStartObject();
				writer.WriteStartArray("settings");
				foreach (var definition in _registry.Definitions) {
					WriteSettingObject(writer, definition);
				}

				writer.WriteEndArray();
				writer.WriteEndObject();
			}

			return Encoding.UTF8.GetString(stream.ToArray());
		}
	}

	/// <summary>Builds the <c>getSetting</c> JSON for one key (resolved value, source, default, apply).</summary>
	public string BuildGetJson(string key) {
		lock (_gate) {
			var definition = _registry.Require(key);
			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream)) {
				WriteSettingObject(writer, definition);
			}

			return Encoding.UTF8.GetString(stream.ToArray());
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
			_watcher.Dispose();
		}

		_debounce?.Dispose();
	}

	private void WriteSettingObject(Utf8JsonWriter writer, SettingDefinition definition) {
		var (value, source) = ResolveLocked(definition);
		var fallback = definition.ComputeDefault?.Invoke() ?? definition.Default;

		writer.WriteStartObject();
		writer.WriteString("key", definition.Key);
		writer.WriteString("type", KindName(definition.Kind));
		writer.WriteString("description", definition.Description);
		writer.WriteStartArray("aliases");
		foreach (var alias in definition.Aliases) {
			writer.WriteStringValue(alias);
		}

		writer.WriteEndArray();
		writer.WritePropertyName("value");
		WriteTypedValue(writer, value);
		writer.WriteString("source", SourceName(source));
		writer.WritePropertyName("default");
		WriteTypedValue(writer, fallback);
		writer.WriteString("apply", ApplyName(definition.Apply));
		if (definition.AllowedValues is not null) {
			writer.WriteStartArray("allowedValues");
			foreach (var allowed in definition.AllowedValues) {
				writer.WriteStringValue(allowed);
			}

			writer.WriteEndArray();
		}

		writer.WriteEndObject();
	}

	private ResolvedValue ResolveLocked(SettingDefinition definition) {
		var env = Environment.GetEnvironmentVariable(definition.EnvVar);
		if (env is not null) {
			if (TryCoerceEnv(definition, env, out var coerced, out var error)) {
				var validation = ValidateValue(definition, coerced);
				if (validation.IsValid) {
					return new ResolvedValue(coerced, SettingSource.Environment);
				}

				error = validation.Message;
			}

			Log?.Invoke($"[settings] {definition.Key}: ignoring invalid {definition.EnvVar}='{env}' ({error}); falling back.");
		}

		if (!_malformed && TryGetFileValue(definition.Key, out var fileValue)) {
			if (TryCoerceFile(definition, fileValue, out var coerced, out var error)) {
				var validation = ValidateValue(definition, coerced);
				if (validation.IsValid) {
					return new ResolvedValue(coerced, SettingSource.UserFile);
				}

				error = validation.Message;
			}

			Log?.Invoke($"[settings] {definition.Key}: ignoring invalid value in {FilePath} ({error}); falling back to default.");
		}

		return new ResolvedValue(definition.ComputeDefault?.Invoke() ?? definition.Default, SettingSource.Default);
	}

	private ValidationResult ValidateValue(SettingDefinition definition, object? value) {
		if (definition.AllowedValues is not null && value is string text
			&& !definition.AllowedValues.Contains(text, StringComparer.Ordinal)) {
			return ValidationResult.Failure(
				$"'{text}' is not allowed. Allowed values: {string.Join(", ", definition.AllowedValues)}.");
		}

		return definition.Validate?.Invoke(value) ?? ValidationResult.Success;
	}

	private bool TryGetFileValue(string key, out object? value) {
		value = null;
		var parts = key.Split('.');
		TomlTable table = _model;
		for (var i = 0; i < parts.Length; i++) {
			if (!table.TryGetValue(parts[i], out var current)) {
				return false;
			}

			if (i == parts.Length - 1) {
				value = current;
				return true;
			}

			if (current is TomlTable next) {
				table = next;
			} else {
				return false; // a leaf where a subtable was expected
			}
		}

		return false;
	}

	private bool TryCoerceEnv(SettingDefinition definition, string raw, out object? value, out string? error) {
		error = null;
		switch (definition.Kind) {
			case SettingKind.String:
				value = raw;
				return true;
			case SettingKind.Path:
				value = NormalizePath(raw, definition.Key);
				return true;
			case SettingKind.Bool:
				if (bool.TryParse(raw, out var b)) {
					value = b;
					return true;
				}

				error = $"expected true/false, got '{raw}'";
				value = null;
				return false;
			case SettingKind.Int:
				if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) {
					value = l;
					return true;
				}

				error = $"expected an integer, got '{raw}'";
				value = null;
				return false;
			default:
				error = $"unsupported kind {definition.Kind}";
				value = null;
				return false;
		}
	}

	private bool TryCoerceFile(SettingDefinition definition, object? raw, out object? value, out string? error) {
		error = null;
		switch (definition.Kind) {
			case SettingKind.String:
				if (raw is string s) {
					value = s;
					return true;
				}

				break;
			case SettingKind.Path:
				if (raw is string p) {
					value = NormalizePath(p, definition.Key);
					return true;
				}

				break;
			case SettingKind.Bool:
				if (raw is bool b) {
					value = b;
					return true;
				}

				break;
			case SettingKind.Int:
				switch (raw) {
					case long l:
						value = l;
						return true;
					case int i:
						value = (long)i;
						return true;
				}

				break;
		}

		error = $"expected {KindName(definition.Kind)}, got {raw?.GetType().Name ?? "null"}";
		value = null;
		return false;
	}

	private bool TryCoerceJson(SettingDefinition definition, JsonElement value, out object? coerced, out string? error) {
		error = null;
		switch (definition.Kind) {
			case SettingKind.String:
				if (value.ValueKind == JsonValueKind.String) {
					coerced = value.GetString();
					return true;
				}

				break;
			case SettingKind.Path:
				if (value.ValueKind == JsonValueKind.String) {
					coerced = NormalizePath(value.GetString()!, definition.Key);
					return true;
				}

				break;
			case SettingKind.Bool:
				if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) {
					coerced = value.GetBoolean();
					return true;
				}

				break;
			case SettingKind.Int:
				if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var l)) {
					coerced = l;
					return true;
				}

				break;
		}

		error = $"setting '{definition.Key}' expects a {KindName(definition.Kind)} value.";
		coerced = null;
		return false;
	}

	private string NormalizePath(string raw, string key) {
		var expanded = raw;
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (expanded == "~") {
			expanded = home;
		} else if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal)) {
			expanded = Path.Combine(home, expanded[2..]);
		}

		if (Path.IsPathRooted(expanded)) {
			return Path.GetFullPath(expanded);
		}

		var baseDir = string.Equals(key, WorkspaceKey, StringComparison.Ordinal)
			? home
			: ResolveWorkspaceDirLocked(home);
		return Path.GetFullPath(Path.Combine(baseDir, expanded));
	}

	private string ResolveWorkspaceDirLocked(string fallback) {
		if (_registry.TryGet(WorkspaceKey, out var workspace) && ResolveLocked(workspace).Value is string dir) {
			return dir;
		}

		return fallback;
	}

	private void SetDocValueLocked(SettingDefinition definition, object? coerced) {
		var valueSyntax = BuildValueSyntax(definition, coerced);
		var existing = FindKeyValueLocked(definition.Key);
		if (existing is not null) {
			existing.Value = valueSyntax;
		} else {
			var keyValue = new KeyValueSyntax(BuildKeySyntax(definition.Key), valueSyntax) {
				// Self-document a newly written key with its description; never touches an existing line,
				// so a comment the user wrote themselves is preserved.
				LeadingTrivia = [
					new SyntaxTrivia(TokenKind.Comment, "# " + definition.Description),
					new SyntaxTrivia(TokenKind.NewLine, "\n"),
				],
			};
			_doc.KeyValues.Add(keyValue);
		}

		_model = _doc.ToModel();
	}

	private KeyValueSyntax? FindKeyValueLocked(string key) {
		foreach (var keyValue in _doc.KeyValues) {
			if (keyValue.Key is { } syntax && string.Equals(DottedKeyName(syntax), key, StringComparison.Ordinal)) {
				return keyValue;
			}
		}

		return null;
	}

	private void SaveAtomicLocked() {
		var text = _doc.ToString();
		var tmp = FilePath + ".tmp";
		File.WriteAllText(tmp, text);
		if (File.Exists(FilePath)) {
			File.Replace(tmp, FilePath, null);
		} else {
			File.Move(tmp, FilePath);
		}
	}

	private List<SettingChange> RecomputeAndDiffLocked() {
		var changes = new List<SettingChange>();
		foreach (var definition in _registry.Definitions) {
			var (value, source) = ResolveLocked(definition);
			var previous = _resolved.GetValueOrDefault(definition.Key);
			if (!Equals(previous, value)) {
				changes.Add(new SettingChange(definition.Key, previous, value, source));
				_resolved[definition.Key] = value;
			}
		}

		return changes;
	}

	private void RaiseChanges(List<SettingChange> changes) {
		foreach (var change in changes) {
			SettingChanged?.Invoke(change);
		}
	}

	private void OnFileEvent(object sender, FileSystemEventArgs e) =>
		_debounce?.Change(250, Timeout.Infinite);

	private void OnDebounceElapsed(object? state) {
		List<SettingChange> changes;
		lock (_gate) {
			if (_disposed) {
				return;
			}

			LoadFromDiskLocked();
			if (_malformed) {
				return; // keep last-good resolved state; don't thrash reactions on a transient typo
			}

			changes = RecomputeAndDiffLocked();
		}

		RaiseChanges(changes);
	}

	private void LoadFromDiskLocked() {
		string text;
		try {
			text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			Log?.Invoke($"[settings] could not read {FilePath}: {ex.Message}; using defaults.");
			text = string.Empty;
		}

		var parsed = Toml.Parse(text, FilePath);
		if (parsed.HasErrors) {
			_malformed = true;
			foreach (var diagnostic in parsed.Diagnostics) {
				Log?.Invoke($"[settings] {diagnostic}");
			}

			Log?.Invoke($"[settings] {FilePath} has parse errors; using defaults and refusing writes until fixed.");
			if (!_hasGoodDoc) {
				// First load is malformed: there is no last-good document, so resolve to all defaults.
				_doc = new DocumentSyntax();
				_model = _doc.ToModel();
			}

			return; // otherwise keep the last good document so live resolution stays stable
		}

		_malformed = false;
		_hasGoodDoc = true;
		_doc = parsed;
		_model = parsed.ToModel();
	}

	private static ValueSyntax BuildValueSyntax(SettingDefinition definition, object? coerced) =>
		definition.Kind switch {
			SettingKind.Bool => new BooleanValueSyntax((bool)coerced!),
			SettingKind.Int => new IntegerValueSyntax((long)coerced!),
			SettingKind.Path => MakeStringSyntax((string)coerced!, preferLiteral: true),
			_ => MakeStringSyntax((string)coerced!, preferLiteral: ((string)coerced!).Contains('\\', StringComparison.Ordinal)),
		};

	// Emit a single-quoted literal string (no escaping — ideal for Windows paths) when the value has
	// no character that a literal string can't hold; otherwise a normal escaped basic string.
	private static StringValueSyntax MakeStringSyntax(string value, bool preferLiteral) {
		if (preferLiteral
			&& !value.Contains('\'', StringComparison.Ordinal)
			&& !value.Contains('\n', StringComparison.Ordinal)
			&& !value.Contains('\r', StringComparison.Ordinal)) {
			return new StringValueSyntax(value) {
				Token = new SyntaxToken(TokenKind.StringLiteral, "'" + value + "'"),
			};
		}

		return new StringValueSyntax(value);
	}

	private static KeySyntax BuildKeySyntax(string key) {
		var parts = key.Split('.');
		return parts.Length switch {
			1 => new KeySyntax(parts[0]),
			2 => new KeySyntax(parts[0], parts[1]),
			_ => throw new NotSupportedException($"Keys deeper than two segments are not supported yet ('{key}')."),
		};
	}

	private static string DottedKeyName(KeySyntax key) {
		var parts = new List<string> { KeyPartName(key.Key) };
		foreach (var dot in key.DotKeys) {
			parts.Add(KeyPartName(dot.Key));
		}

		return string.Join('.', parts);
	}

	private static string KeyPartName(BareKeyOrStringValueSyntax? part) => part switch {
		BareKeySyntax bare => bare.Key?.Text?.Trim() ?? string.Empty,
		StringValueSyntax str => str.Value ?? string.Empty,
		_ => part?.ToString()?.Trim() ?? string.Empty,
	};

	private static void WriteTypedValue(Utf8JsonWriter writer, object? value) {
		switch (value) {
			case null:
				writer.WriteNullValue();
				break;
			case string s:
				writer.WriteStringValue(s);
				break;
			case bool b:
				writer.WriteBooleanValue(b);
				break;
			case long l:
				writer.WriteNumberValue(l);
				break;
			case int i:
				writer.WriteNumberValue(i);
				break;
			default:
				writer.WriteStringValue(value.ToString());
				break;
		}
	}

	private static string KindName(SettingKind kind) => kind switch {
		SettingKind.String => "string",
		SettingKind.Bool => "bool",
		SettingKind.Int => "int",
		SettingKind.Path => "path",
		_ => kind.ToString().ToLowerInvariant(),
	};

	private static string SourceName(SettingSource source) => source switch {
		SettingSource.Environment => "environment",
		SettingSource.UserFile => "userFile",
		SettingSource.Default => "default",
		_ => source.ToString().ToLowerInvariant(),
	};

	private static string ApplyName(ApplyMode apply) => apply switch {
		ApplyMode.Live => "live",
		ApplyMode.ReopensTerminal => "reopensTerminal",
		ApplyMode.NextSession => "nextSession",
		ApplyMode.RestartRequired => "restartRequired",
		_ => apply.ToString().ToLowerInvariant(),
	};

	private sealed class Subscription : IDisposable {
		private Action? _unsubscribe;

		public Subscription(Action unsubscribe) {
			_unsubscribe = unsubscribe;
		}

		public void Dispose() {
			_unsubscribe?.Invoke();
			_unsubscribe = null;
		}
	}
}
