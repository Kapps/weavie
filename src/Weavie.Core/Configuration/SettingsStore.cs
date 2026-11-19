using System.Globalization;
using System.Text;
using System.Text.Json;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;

namespace Weavie.Core.Configuration;

/// <summary>A resolved setting value and the layer it came from.</summary>
public readonly record struct ResolvedValue(object? Value, SettingSource Source);

/// <summary>The outcome of a <see cref="SettingsStore.Set(string, JsonElement)"/> — what to tell the user.</summary>
public sealed record SetResult {
	/// <summary>Whether the value was written to its backing file (always true on success).</summary>
	public required bool Written { get; init; }

	/// <summary>The env var that overrides the file (so the effective value is unchanged), or <c>null</c>.</summary>
	public string? ShadowedByEnv { get; init; }

	/// <summary>How the change takes effect.</summary>
	public required ApplyMode Apply { get; init; }
}

/// <summary>The outcome of a <see cref="SettingsStore.Clear(string)"/> — what to tell the user.</summary>
public sealed record ClearResult {
	/// <summary>Whether a file override was actually present and removed.</summary>
	public required bool Removed { get; init; }

	/// <summary>The env var that still overrides the resolved value (so it's unchanged), or <c>null</c>.</summary>
	public string? ShadowedByEnv { get; init; }

	/// <summary>How the change takes effect.</summary>
	public required ApplyMode Apply { get; init; }
}

/// <summary>
/// A single resolved-value change, raised to subscribers of <see cref="SettingsStore.SettingChanged"/>.
/// <see cref="WorkspaceRoot"/> is the workspace a <see cref="SettingOwner.Repo"/> change belongs to,
/// or <c>null</c> for a cross-workspace (user-file/env) change.
/// </summary>
public readonly record struct SettingChange(string Key, object? OldValue, object? NewValue, SettingSource Source, string? WorkspaceRoot);

/// <summary>
/// Loads, resolves, and persists settings as TOML and is the change hub the host reacts to. Device and machine
/// keys live in the shared user file (<c>~/.weavie/settings.toml</c>);
/// <see cref="SettingOwner.Repo"/> keys live in each registered workspace's out-of-repo
/// overlay (<c>~/.weavie/workspaces/&lt;id&gt;/settings.toml</c>). Resolution precedence is env var → workspace file → user file → registered
/// default; values are coerced to their declared <see cref="SettingKind"/> and validated. Writes go through
/// Tomlyn's comment-preserving document so unknown subtrees and user comments survive a round-trip. The shared
/// validated-file projection turns hand-edits into <see cref="SettingChanged"/> events, diffing against in-memory
/// state so self-writes never double-fire. See <c>docs/specs/settings.md</c>.
/// </summary>
public sealed class SettingsStore : IDisposable {
	private readonly Func<string, string> _workspaceSettingsPath;
	private readonly Lock _gate = new();
	private readonly bool _enableWatcher;
	private readonly SettingsFileLayer _userLayer;
	private readonly Dictionary<string, WorkspaceRegistration> _workspaces = new(StringComparer.Ordinal);
	private readonly Dictionary<string, object?> _resolved = new(StringComparer.Ordinal);

	private bool _lastMalformed;
	private bool _disposed;

	/// <summary>
	/// Creates a store over <paramref name="filePath"/> (default <c>~/.weavie/settings.toml</c>), loading
	/// current values and — unless <paramref name="enableWatcher"/> is false — watching files for edits.
	/// <paramref name="workspaceSettingsPath"/> maps a workspace root to its out-of-repo overlay file. Register
	/// per-workspace overlays afterwards with <see cref="RegisterWorkspace"/>.
	/// </summary>
	public SettingsStore(SettingsRegistry registry, string? filePath, bool enableWatcher, Func<string, string> workspaceSettingsPath) {
		ArgumentNullException.ThrowIfNull(registry);
		ArgumentNullException.ThrowIfNull(workspaceSettingsPath);
		Registry = registry;
		_workspaceSettingsPath = workspaceSettingsPath;
		_enableWatcher = enableWatcher;
		FilePath = filePath ?? WeaviePaths.SettingsFile;

		string? directory = Path.GetDirectoryName(FilePath);
		if (!string.IsNullOrEmpty(directory)) {
			Directory.CreateDirectory(directory);
		}
		_userLayer = CreateLayer(FilePath);

		lock (_gate) {
			foreach (var definition in Registry.Definitions) {
				_resolved[definition.Key] = ResolveLocked(definition, workspaceRoot: null).Value;
			}

			_lastMalformed = AnyMalformedLocked();
		}

		if (enableWatcher) {
			_userLayer.Watch();
		}
	}

	/// <summary>Raised (off the UI thread) once per resolved value that actually changed.</summary>
	public event Action<SettingChange>? SettingChanged;

	/// <summary>The definitions this store resolves.</summary>
	public SettingsRegistry Registry { get; }

	/// <summary>Diagnostic log line: parse errors and ignored invalid values.</summary>
	public event Action<string>? Log;

	/// <summary>Raised when the malformed state of any watched file flips on a live reload — true once a file
	/// starts having parse errors (its edits ignored), false once every file parses cleanly again.</summary>
	public event Action<bool>? MalformedChanged;

	/// <summary>The user settings file backing this store's cross-workspace layer.</summary>
	public string FilePath { get; }

	/// <summary>Whether any watched settings file currently has TOML parse errors (writes to it are refused while true).</summary>
	public bool IsMalformed {
		get { lock (_gate) { return AnyMalformedLocked(); } }
	}

	/// <summary>
	/// Registers a workspace so its out-of-repo settings overlay is loaded and (if watching is on)
	/// watched, backing <see cref="SettingOwner.Repo"/> resolution for <paramref name="workspaceRoot"/>.
	/// Idempotent per root; the caller reads current values fresh after registering.
	/// </summary>
	public void RegisterWorkspace(string workspaceRoot) {
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		string root = NormalizeRoot(workspaceRoot);
		lock (_gate) {
			if (_disposed || _workspaces.ContainsKey(root)) {
				return;
			}

			var registration = new WorkspaceRegistration(root, CreateLayer(_workspaceSettingsPath(root)));
			_workspaces[root] = registration; // register before seeding so ResolveLocked sees the overlay
			SeedWorkspaceResolvedLocked(registration);
			if (_enableWatcher) {
				registration.Layer.Watch();
			}
			_lastMalformed = AnyMalformedLocked();
		}
	}

	/// <summary>Resolves <paramref name="key"/> through env → user file → default (no workspace overlay).</summary>
	public ResolvedValue Resolve(string key) {
		lock (_gate) {
			return ResolveLocked(Registry.Require(key), workspaceRoot: null);
		}
	}

	/// <summary>Resolves <paramref name="key"/> for <paramref name="workspaceRoot"/> through env → workspace file → user file → default.</summary>
	public ResolvedValue Resolve(string key, string workspaceRoot) {
		lock (_gate) {
			return ResolveLocked(Registry.Require(key), workspaceRoot);
		}
	}

	/// <summary>Resolves <paramref name="key"/> as a string (null if the value is absent or not a string).</summary>
	public string? GetString(string key) => Resolve(key).Value as string;

	/// <summary>Resolves <paramref name="key"/> as a string for <paramref name="workspaceRoot"/> (null if absent or not a string).</summary>
	public string? GetString(string key, string workspaceRoot) => Resolve(key, workspaceRoot).Value as string;

	/// <summary>
	/// Resolves <paramref name="key"/> as a bool, trusting the registered default (no literal fallback). Throws
	/// if it isn't a bool — only possible if registered with a non-bool default, a programming error.
	/// </summary>
	public bool RequireBool(string key) => Resolve(key).Value is bool b ? b : throw WrongKind(key, "bool");

	/// <summary>As <see cref="RequireBool"/>, for an integer setting.</summary>
	public long RequireInt(string key) => Resolve(key).Value is long l ? l : throw WrongKind(key, "int");

	/// <summary>Resolves a finite numeric setting, trusting its registered default.</summary>
	public double RequireNumber(string key) => Resolve(key).Value is double d ? d : throw WrongKind(key, "number");

	/// <summary>As <see cref="RequireBool"/>, for a string setting.</summary>
	public string RequireString(string key) => Resolve(key).Value is string s ? s : throw WrongKind(key, "string");

	/// <summary>Resolves a workspace-scoped string setting, trusting its registered default.</summary>
	public string RequireString(string key, string workspaceRoot) =>
		Resolve(key, workspaceRoot).Value is string s ? s : throw WrongKind(key, "string");

	private static InvalidOperationException WrongKind(string key, string kind) =>
		new($"setting '{key}' did not resolve to a {kind}; check the default it was registered with.");

	/// <summary>
	/// Validates and writes <paramref name="key"/> = <paramref name="value"/> to the user file (the
	/// cross-workspace layer), raising <see cref="SettingChanged"/> for any effective value that changed.
	/// Workspace-scoped keys written here land in the user file as the shared fallback; route them to a
	/// workspace with <see cref="Set(string, JsonElement, string)"/>.
	/// </summary>
	public SetResult Set(string key, JsonElement value) => SetOnLayer(key, value, workspaceRoot: null);

	/// <summary>
	/// As <see cref="Set(string, JsonElement)"/>, but a <see cref="SettingOwner.Repo"/> key is written to
	/// <paramref name="workspaceRoot"/>'s out-of-repo overlay (registering the workspace if needed);
	/// any other key ignores <paramref name="workspaceRoot"/> and writes the user file.
	/// </summary>
	public SetResult Set(string key, JsonElement value, string workspaceRoot) {
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		return SetOnLayer(key, value, workspaceRoot);
	}

	/// <summary>Removes <paramref name="key"/>'s user-file override, falling back to env/default. The inverse of <see cref="Set(string, JsonElement)"/>.</summary>
	public ClearResult Clear(string key) => ClearOnLayer(key, workspaceRoot: null);

	/// <summary>As <see cref="Clear(string)"/>, but a workspace-scoped key is cleared from <paramref name="workspaceRoot"/>'s file.</summary>
	public ClearResult Clear(string key, string workspaceRoot) {
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		return ClearOnLayer(key, workspaceRoot);
	}

	private SetResult SetOnLayer(string key, JsonElement value, string? workspaceRoot) {
		List<SettingChange> changes;
		SetResult result;
		lock (_gate) {
			var definition = Registry.Require(key);
			var layer = TargetLayerLocked(definition, workspaceRoot, createIfMissing: true);
			if (layer.Malformed) {
				throw new SettingsFileMalformedException(
					$"{layer.FilePath} has TOML parse errors; fix or delete it before changing settings.");
			}

			if (!TryCoerceJson(definition, value, out object? coerced, out string? coerceError)) {
				throw new SettingValidationException(key, coerceError!);
			}

			var validation = ValidateValue(definition, coerced);
			if (!validation.IsValid) {
				throw new SettingValidationException(key, validation.Message ?? "invalid value");
			}

			object? previous = ResolveLocked(definition, workspaceRoot).Value;
			layer.SetValue(definition, coerced);
			ResetDependentsLocked(definition, layer, workspaceRoot, previous);
			if (_enableWatcher) {
				layer.Watch();
			}
			changes = RecomputeAndDiffLocked();

			string? shadow = ResolveLocked(definition, workspaceRoot).Source == SettingSource.Environment ? definition.EnvVar : null;
			result = new SetResult { Written = true, ShadowedByEnv = shadow, Apply = definition.Apply };
		}

		RaiseChanges(changes);
		return result;
	}

	private ClearResult ClearOnLayer(string key, string? workspaceRoot) {
		List<SettingChange> changes;
		ClearResult result;
		lock (_gate) {
			var definition = Registry.Require(key);
			var layer = TargetLayerLocked(definition, workspaceRoot, createIfMissing: false);
			if (layer.Malformed) {
				throw new SettingsFileMalformedException(
					$"{layer.FilePath} has TOML parse errors; fix or delete it before changing settings.");
			}

			object? previous = ResolveLocked(definition, workspaceRoot).Value;
			bool removed = layer.RemoveKey(definition.Key);
			ResetDependentsLocked(definition, layer, workspaceRoot, previous);

			changes = RecomputeAndDiffLocked();
			string? shadow = ResolveLocked(definition, workspaceRoot).Source == SettingSource.Environment ? definition.EnvVar : null;
			result = new ClearResult { Removed = removed, ShadowedByEnv = shadow, Apply = definition.Apply };
		}

		RaiseChanges(changes);
		return result;
	}

	private void ResetDependentsLocked(
		SettingDefinition definition,
		SettingsFileLayer layer,
		string? workspaceRoot,
		object? previous) {
		if (Equals(previous, ResolveLocked(definition, workspaceRoot).Value)) {
			return;
		}

		foreach (string key in definition.Resets) {
			layer.RemoveKey(key);
		}
	}

	// The file a write targets: a Repo key with a root goes to that workspace's overlay (registered
	// on demand for writes); everything else — including a Repo key with no root — goes to the user file.
	private SettingsFileLayer TargetLayerLocked(SettingDefinition definition, string? workspaceRoot, bool createIfMissing) {
		if (definition.Owner != SettingOwner.Repo || workspaceRoot is null) {
			return _userLayer;
		}

		string root = NormalizeRoot(workspaceRoot);
		if (_workspaces.TryGetValue(root, out var existing)) {
			return existing.Layer;
		}

		if (!createIfMissing) {
			return _userLayer; // nothing registered and not creating: clearing a never-set overlay is a no-op on the user file
		}

		var registration = new WorkspaceRegistration(root, CreateLayer(_workspaceSettingsPath(root)));
		_workspaces[root] = registration; // register before seeding so ResolveLocked sees the overlay
		SeedWorkspaceResolvedLocked(registration);
		return registration.Layer;
	}

	/// <summary>Subscribes <paramref name="handler"/> to changes of a single <paramref name="key"/> (across all workspaces).</summary>
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

	/// <summary>Builds the <c>listSettings</c> index JSON (key, type, aliases, value, source, allowed values) with values resolved through env → user file → default (no workspace overlay).</summary>
	public string BuildIndexJson() => BuildIndexJson(workspaceRoot: null, static _ => true);

	/// <summary>As <see cref="BuildIndexJson()"/> for the settings <paramref name="include"/> admits, resolving repo keys against <paramref name="workspaceRoot"/>'s overlay when given.</summary>
	public string BuildIndexJson(string? workspaceRoot, Func<SettingDefinition, bool> include) {
		ArgumentNullException.ThrowIfNull(include);
		lock (_gate) {
			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream)) {
				writer.WriteStartObject();
				writer.WriteStartArray("settings");
				foreach (var definition in Registry.Definitions.Where(include)) {
					WriteSettingObject(writer, definition, workspaceRoot, detailed: false);
				}

				writer.WriteEndArray();
				writer.WriteEndObject();
			}

			return Encoding.UTF8.GetString(stream.ToArray());
		}
	}

	/// <summary>Builds the <c>getSetting</c> JSON (the full entry) for one key resolved through env → user file → default (no workspace overlay).</summary>
	public string BuildGetJson(string key) => BuildGet(key, workspaceRoot: null);

	/// <summary>As <see cref="BuildGetJson(string)"/>, resolving a <see cref="SettingOwner.Repo"/> key against <paramref name="workspaceRoot"/>'s overlay.</summary>
	public string BuildGetJson(string key, string workspaceRoot) => BuildGet(key, workspaceRoot);

	private string BuildGet(string key, string? workspaceRoot) {
		lock (_gate) {
			var definition = Registry.Require(key);
			using var stream = new MemoryStream();
			using (var writer = new Utf8JsonWriter(stream)) {
				WriteSettingObject(writer, definition, workspaceRoot, detailed: true);
			}

			return Encoding.UTF8.GetString(stream.ToArray());
		}
	}

	/// <inheritdoc/>
	public void Dispose() {
		SettingsFileLayer[] layers;
		lock (_gate) {
			if (_disposed) {
				return;
			}

			_disposed = true;
			layers = [_userLayer, .. _workspaces.Values.Select(workspace => workspace.Layer)];
		}

		foreach (var layer in layers) {
			layer.Dispose();
		}
	}

	// The listSettings index omits what getSetting adds: description, default, and apply.
	private void WriteSettingObject(Utf8JsonWriter writer, SettingDefinition definition, string? workspaceRoot, bool detailed) {
		var (value, source) = ResolveLocked(definition, workspaceRoot);

		writer.WriteStartObject();
		writer.WriteString("key", definition.Key);
		writer.WriteString("type", KindName(definition.Kind));
		if (detailed) {
			writer.WriteString(
				"description",
				definition.Resets.Count == 0
					? definition.Description
					: $"{definition.Description} Changing it clears {string.Join(", ", definition.Resets)}.");
		}

		writer.WriteString("owner", OwnerName(definition.Owner));

		writer.WriteStartArray("aliases");
		foreach (string alias in definition.Aliases) {
			writer.WriteStringValue(alias);
		}

		writer.WriteEndArray();
		writer.WritePropertyName("value");
		WriteTypedValue(writer, value);
		writer.WriteString("source", SourceName(source));
		if (detailed) {
			writer.WritePropertyName("default");
			WriteTypedValue(writer, definition.ComputeDefault?.Invoke() ?? definition.Default);
			writer.WriteString("apply", ApplyName(definition.Apply));
		}

		if (definition.AllowedValues is not null) {
			writer.WriteStartArray("allowedValues");
			foreach (string allowed in definition.AllowedValues) {
				writer.WriteStringValue(allowed);
			}

			writer.WriteEndArray();
		}

		writer.WriteEndObject();
	}

	private ResolvedValue ResolveLocked(SettingDefinition definition, string? workspaceRoot) {
		string? env = Environment.GetEnvironmentVariable(definition.EnvVar);
		if (env is not null) {
			if (TryCoerceEnv(definition, env, out object? coerced, out string? error)) {
				var validation = ValidateValue(definition, coerced);
				if (validation.IsValid) {
					return new ResolvedValue(coerced, SettingSource.Environment);
				}

				error = validation.Message;
			}

			Log?.Invoke($"[settings] {definition.Key}: ignoring invalid {definition.EnvVar}='{env}' ({error}); falling back.");
		}

		if (definition.Owner == SettingOwner.Repo && workspaceRoot is not null
			&& _workspaces.TryGetValue(NormalizeRoot(workspaceRoot), out var registration)
			&& registration.Layer.TryGetValue(definition.Key, out object? wsValue)) {
			if (TryCoerceFile(definition, wsValue, out object? coerced, out string? error)) {
				var validation = ValidateValue(definition, coerced);
				if (validation.IsValid) {
					return new ResolvedValue(coerced, SettingSource.WorkspaceFile);
				}

				error = validation.Message;
			}

			Log?.Invoke($"[settings] {definition.Key}: ignoring invalid value in {registration.Layer.FilePath} ({error}); falling back.");
		}

		if (_userLayer.TryGetValue(definition.Key, out object? fileValue)) {
			if (TryCoerceFile(definition, fileValue, out object? coerced, out string? error)) {
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

	private bool TryCoerceEnv(SettingDefinition definition, string raw, out object? value, out string? error) {
		error = null;
		switch (definition.Kind) {
			case SettingKind.String or SettingKind.Json:
				value = raw;
				return true;
			case SettingKind.Path:
				value = NormalizePath(raw, definition.Key);
				return true;
			case SettingKind.Bool:
				if (bool.TryParse(raw, out bool b)) {
					value = b;
					return true;
				}

				error = $"expected true/false, got '{raw}'";
				value = null;
				return false;
			case SettingKind.Number:
				if (TryNumber(raw, out double number)) {
					value = number;
					return true;
				}
				error = "expected a finite number";
				value = null;
				return false;
			case SettingKind.Int:
				if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) {
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
			case SettingKind.String or SettingKind.Json:
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
			case SettingKind.Number:
				if (raw is long or int or double && double.IsFinite(Convert.ToDouble(raw, CultureInfo.InvariantCulture))) {
					value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
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

				// Tolerate a stringified bool ("true"/"false"): LLM tool calls routinely stringify scalars.
				// A non-bool string still falls through and is rejected below.
				if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out bool sb)) {
					coerced = sb;
					return true;
				}

				break;
			case SettingKind.Number:
				if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number)
					|| value.ValueKind == JsonValueKind.String && TryNumber(value.GetString()!, out number)) {
					coerced = number;
					return true;
				}
				break;
			case SettingKind.Int:
				if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long l)) {
					coerced = l;
					return true;
				}

				// Tolerate a stringified integer ("16") for the same reason; a non-numeric string
				// ("NaN") still falls through to the rejection below.
				if (value.ValueKind == JsonValueKind.String
					&& long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long sl)) {
					coerced = sl;
					return true;
				}

				break;
			case SettingKind.Json:
				// A client that parses the value before sending it delivers the JSON itself, never its text.
				coerced = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
				return true;
		}

		error = $"setting '{definition.Key}' expects a {KindName(definition.Kind)} value.";
		coerced = null;
		return false;
	}

	private static bool TryNumber(string raw, out double number) =>
		double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);

	private string NormalizePath(string raw, string key) {
		string expanded = WorkspacePaths.ExpandHome(raw);
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

		// A Windows drive-rooted path isn't rooted on POSIX; resolving it against a Linux base dir would
		// mangle it. The settings file crosses machines (a remote runner reads a laptop's), so pass it through.
		if (!OperatingSystem.IsWindows() && IsWindowsDriveRooted(expanded)) {
			return expanded;
		}

		if (Path.IsPathRooted(expanded)) {
			return Path.GetFullPath(expanded);
		}

		// A drive-letter path on a Unix host: Path.IsPathRooted is OS-local, and GetFullPath would resolve
		// it against the workspace dir — keep it verbatim.
		if (expanded.Length >= 3 && char.IsAsciiLetter(expanded[0]) && expanded[1] == ':' && expanded[2] is '\\' or '/') {
			return expanded;
		}

		string baseDir = string.Equals(key, CoreSettings.Workspace, StringComparison.Ordinal)
			? home
			: ResolveWorkspaceDirLocked(home);
		return Path.GetFullPath(Path.Combine(baseDir, expanded));
	}

	private static bool IsWindowsDriveRooted(string path) =>
		path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

	private string ResolveWorkspaceDirLocked(string fallback) {
		if (Registry.TryGet(CoreSettings.Workspace, out var workspace) && ResolveLocked(workspace, workspaceRoot: null).Value is string dir) {
			return dir;
		}

		return fallback;
	}

	private static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

	// Seeds a workspace's resolved cache so a later reload can diff against it and only fire real changes.
	private void SeedWorkspaceResolvedLocked(WorkspaceRegistration registration) {
		foreach (var definition in Registry.Definitions) {
			if (definition.Owner == SettingOwner.Repo) {
				registration.Resolved[definition.Key] = ResolveLocked(definition, registration.Root).Value;
			}
		}
	}

	private List<SettingChange> RecomputeAndDiffLocked() {
		var changes = new List<SettingChange>();
		foreach (var definition in Registry.Definitions) {
			var (value, source) = ResolveLocked(definition, workspaceRoot: null);
			object? previous = _resolved.GetValueOrDefault(definition.Key);
			if (!Equals(previous, value)) {
				changes.Add(new SettingChange(definition.Key, previous, value, source, WorkspaceRoot: null));
				_resolved[definition.Key] = value;
			}
		}

		foreach (var registration in _workspaces.Values) {
			DiffWorkspaceLocked(registration, changes);
		}

		return changes;
	}

	private void DiffWorkspaceLocked(WorkspaceRegistration registration, List<SettingChange> changes) {
		foreach (var definition in Registry.Definitions) {
			if (definition.Owner != SettingOwner.Repo) {
				continue;
			}

			var (value, source) = ResolveLocked(definition, registration.Root);
			object? previous = registration.Resolved.GetValueOrDefault(definition.Key);
			if (!Equals(previous, value)) {
				changes.Add(new SettingChange(definition.Key, previous, value, source, registration.Root));
				registration.Resolved[definition.Key] = value;
			}
		}
	}

	private void RaiseChanges(List<SettingChange> changes) {
		foreach (var change in changes) {
			SettingChanged?.Invoke(change);
		}
	}

	private bool AnyMalformedLocked() {
		if (_userLayer.Malformed) {
			return true;
		}

		foreach (var registration in _workspaces.Values) {
			if (registration.Layer.Malformed) {
				return true;
			}
		}

		return false;
	}

	private SettingsFileLayer CreateLayer(string filePath) {
		var layer = new SettingsFileLayer(filePath, _gate);
		layer.Reloaded += reload => OnLayerReloaded(layer, reload);
		return layer;
	}

	private void OnLayerReloaded(
		SettingsFileLayer layer,
		FileReload<SettingsFileLayer.SettingsDocument> reload) {
		List<SettingChange> changes;
		bool malformedFlipped;
		bool nowMalformed;
		lock (_gate) {
			if (_disposed) {
				return;
			}

			nowMalformed = AnyMalformedLocked();
			malformedFlipped = _lastMalformed != nowMalformed;
			_lastMalformed = nowMalformed;
			changes = reload.Error is null ? RecomputeAndDiffLocked() : [];
		}
		if (reload.Error is not null) {
			Log?.Invoke($"[settings] {layer.FilePath} is invalid ({reload.Error.Message}); keeping the last-good settings until fixed.");
		}

		if (malformedFlipped) {
			MalformedChanged?.Invoke(nowMalformed);
		}

		RaiseChanges(changes);
	}

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
			case double d:
				writer.WriteNumberValue(d);
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
		SettingKind.Number => "number",
		SettingKind.Path => "path",
		SettingKind.Json => "json",
		_ => kind.ToString().ToLowerInvariant(),
	};

	private static string OwnerName(SettingOwner owner) => owner switch {
		SettingOwner.Device => "device",
		SettingOwner.Machine => "machine",
		SettingOwner.Repo => "repo",
		_ => throw new ArgumentOutOfRangeException(nameof(owner), owner, "Unknown setting owner."),
	};

	private static string SourceName(SettingSource source) => source switch {
		SettingSource.Environment => "environment",
		SettingSource.WorkspaceFile => "workspaceFile",
		SettingSource.UserFile => "userFile",
		SettingSource.Default => "default",
		_ => source.ToString().ToLowerInvariant(),
	};

	private static string ApplyName(ApplyMode apply) => apply switch {
		ApplyMode.Live => "live",
		ApplyMode.ReopensTerminal => "reopensTerminal",
		ApplyMode.NextSession => "nextSession",
		ApplyMode.NextLanguageServerStart => "nextLanguageServerStart",
		ApplyMode.RestartRequired => "restartRequired",
		_ => apply.ToString().ToLowerInvariant(),
	};

	// One registered workspace's overlay: its file layer plus the resolved snapshot a reload diffs against.
	private sealed class WorkspaceRegistration {
		public WorkspaceRegistration(string root, SettingsFileLayer layer) {
			Root = root;
			Layer = layer;
		}

		public string Root { get; }

		public SettingsFileLayer Layer { get; }

		public Dictionary<string, object?> Resolved { get; } = new(StringComparer.Ordinal);

	}

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
