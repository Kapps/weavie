namespace Weavie.Core.Configuration;

/// <summary>
/// The declared type of a setting — the authority for how a raw value is parsed, coerced, and normalized. A
/// kind exists only where it carries distinct parse/normalize behavior; a mere constraint (allowed set, range,
/// must-exist-on-disk) is a validator. See <c>docs/specs/settings.md</c> § "Kinds vs constraints".
/// </summary>
public enum SettingKind {
	/// <summary>A verbatim text value.</summary>
	String,

	/// <summary>A boolean; parsed case-insensitively from <c>true</c>/<c>false</c>.</summary>
	Bool,

	/// <summary>A 64-bit integer; parsed with invariant culture.</summary>
	Int,

	/// <summary>A filesystem path: <c>~</c> is expanded and a relative path is resolved against the workspace.</summary>
	Path,
}

/// <summary>
/// How a changed value takes effect. Only <see cref="ReopensTerminal"/> has an active reaction wired; the
/// others are read fresh when the relevant session next starts.
/// </summary>
public enum ApplyMode {
	/// <summary>Observers reflect it immediately; nothing restarts.</summary>
	Live,

	/// <summary>The affected terminal pane(s) restart to apply (e.g. the shell).</summary>
	ReopensTerminal,

	/// <summary>Existing sessions keep the old value; the next one started picks it up.</summary>
	NextSession,

	/// <summary>Needs a full app restart.</summary>
	RestartRequired,
}

/// <summary>Which resolution layer a value came from (highest precedence first).</summary>
public enum SettingSource {
	/// <summary>A <c>WEAVIE_*</c> environment variable (wins over any file).</summary>
	Environment,

	/// <summary>The workspace's out-of-repo overlay <c>~/.weavie/workspaces/&lt;id&gt;/settings.toml</c> (only for <see cref="SettingScope.Workspace"/> keys).</summary>
	WorkspaceFile,

	/// <summary>The user's <c>settings.toml</c>.</summary>
	UserFile,

	/// <summary>The registered default (computed or static).</summary>
	Default,
}

/// <summary>
/// Where a setting is stored and resolved from. <see cref="User"/> keys live in the shared user file
/// (one value across every workspace, e.g. theme); <see cref="Workspace"/> keys live in the workspace's
/// out-of-repo overlay (<c>~/.weavie/workspaces/&lt;id&gt;/settings.toml</c>) and are resolved per workspace
/// (e.g. the test profile), falling back to the user file when the workspace hasn't set them. See
/// <c>docs/specs/settings.md</c>.
/// </summary>
public enum SettingScope {
	/// <summary>Cross-workspace: stored in the user file, one value everywhere.</summary>
	User,

	/// <summary>Per-workspace: stored out-of-repo in <c>~/.weavie/workspaces/&lt;id&gt;/settings.toml</c>, resolved against the active workspace.</summary>
	Workspace,
}

/// <summary>
/// The outcome of an open-ended <see cref="SettingDefinition.Validate"/> check: valid, or invalid with a
/// human-readable reason that surfaces to the user.
/// </summary>
public readonly record struct ValidationResult {
	private ValidationResult(bool isValid, string? message) {
		IsValid = isValid;
		Message = message;
	}

	/// <summary>Whether the value passed validation.</summary>
	public bool IsValid { get; }

	/// <summary>The rejection reason when <see cref="IsValid"/> is <c>false</c>; otherwise <c>null</c>.</summary>
	public string? Message { get; }

	/// <summary>A passing result.</summary>
	public static ValidationResult Success { get; } = new(true, null);

	/// <summary>A failing result carrying the reason <paramref name="message"/>.</summary>
	public static ValidationResult Failure(string message) => new(false, message);
}

/// <summary>
/// A declared setting: the single source of truth for what exists, its kind, default, documentation,
/// validation, derived env var, and how a change applies — driving defaults, the file comment, env-var
/// overrides, the MCP tool surface, and Claude's NL mapping. See <c>docs/specs/settings.md</c>.
/// </summary>
public sealed record SettingDefinition {
	/// <summary>The dotted key, e.g. <c>terminal.shell</c>. Unique within the registry.</summary>
	public required string Key { get; init; }

	/// <summary>The declared kind, authoritative for parsing/coercion.</summary>
	public required SettingKind Kind { get; init; }

	/// <summary>Human-readable description — surfaced to Claude and injected as the file's <c>#</c> comment.</summary>
	public required string Description { get; init; }

	/// <summary>Natural-language hints (e.g. <c>"shell"</c>, <c>"my shell"</c>) that help Claude map intent to this key.</summary>
	public IReadOnlyList<string> Aliases { get; init; } = [];

	/// <summary>
	/// A closed set of permitted values for a <see cref="SettingKind.String"/> setting — enumerable and
	/// auto-validated. Use this instead of <see cref="Validate"/> whenever the options can be listed.
	/// </summary>
	public IReadOnlyList<string>? AllowedValues { get; init; }

	/// <summary>A static default value, used when neither the env var nor the file provides one.</summary>
	public object? Default { get; init; }

	/// <summary>A computed default (e.g. platform auto-detection), preferred over <see cref="Default"/> when set.</summary>
	public Func<object?>? ComputeDefault { get; init; }

	/// <summary>
	/// An open-ended validation predicate for checks that cannot be enumerated (e.g. "resolvable on PATH").
	/// Closed sets belong in <see cref="AllowedValues"/> instead.
	/// </summary>
	public Func<object?, ValidationResult>? Validate { get; init; }

	/// <summary>How a change to this setting takes effect.</summary>
	public ApplyMode Apply { get; init; } = ApplyMode.NextSession;

	/// <summary>Where the value is stored and resolved from — the shared user file or the workspace's out-of-repo overlay.</summary>
	public SettingScope Scope { get; init; } = SettingScope.User;

	/// <summary>
	/// The derived override env var: <c>WEAVIE_</c> + the key uppercased with <c>.</c> → <c>_</c>
	/// (e.g. <c>terminal.shell</c> → <c>WEAVIE_TERMINAL_SHELL</c>).
	/// </summary>
	public string EnvVar => "WEAVIE_" + Key.ToUpperInvariant().Replace('.', '_');
}
