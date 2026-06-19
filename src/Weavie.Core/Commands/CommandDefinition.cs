namespace Weavie.Core.Commands;

/// <summary>
/// Which world executes a command's handler. The declaration always lives in Core (so the MCP surface
/// and the palette see every command), but the handler is registered on whichever side owns the action.
/// </summary>
public enum CommandLocation {
	/// <summary>The handler lives in the Solid web app (focus a pane, toggle the diff layout, open the omnibar).</summary>
	Web,

	/// <summary>The handler lives in Core/host (reopen the terminal, change the layout, …).</summary>
	Core,
}

/// <summary>The outcome of running a command — reported to Claude by <c>runCommand</c> and honored by the caller.</summary>
public readonly record struct CommandResult(bool Ok, string? Message, string? Error) {
	/// <summary>A successful run, optionally carrying a human-readable confirmation.</summary>
	public static CommandResult Success(string? message = null) => new(true, message, null);

	/// <summary>A failed run carrying the reason <paramref name="error"/>.</summary>
	public static CommandResult Failure(string error) => new(false, null, error);
}

/// <summary>
/// A default keybinding declared by a command. The <see cref="Key"/> uses the tinykeys-style
/// cross-platform <c>$mod</c> token (Cmd on macOS, Ctrl elsewhere); <see cref="ArgsJson"/> lets one
/// command (e.g. focus-pane-by-index) carry per-binding arguments.
/// </summary>
public sealed record CommandKeybinding {
	/// <summary>The chord, e.g. <c>$mod+1</c> or <c>$mod+Shift+p</c>.</summary>
	public required string Key { get; init; }

	/// <summary>Optional raw-JSON argument object for this binding, e.g. <c>{"index":1}</c>.</summary>
	public string? ArgsJson { get; init; }

	/// <summary>
	/// When true, this is an OS-level <em>global</em> hotkey: the host registers it with the operating
	/// system so it fires even when Weavie isn't the focused application (e.g. <c>ctrl+`</c> → focus the
	/// window). The web keydown resolver ignores global bindings — the OS owns them — so they never
	/// double-fire while Weavie is focused. See <c>docs/specs/commands.md</c>.
	/// </summary>
	public bool Global { get; init; }
}

/// <summary>
/// A declared command: a named action Weavie can perform. The single source of truth for what exists,
/// where it runs, its default keybinding(s), palette visibility, <c>when</c> guard, and the docs/aliases
/// Claude uses to map natural language onto the exact id. One declaration drives the MCP tool surface,
/// the keybinding defaults, and the omnibar command palette. See <c>docs/specs/commands.md</c>.
/// </summary>
public sealed record CommandDefinition {
	/// <summary>The stable, namespaced id, e.g. <c>weavie.diff.toggleLayout</c>. Unique within the registry.</summary>
	public required string Id { get; init; }

	/// <summary>The palette label, e.g. "Toggle Diff: Inline / Side-by-Side".</summary>
	public required string Title { get; init; }

	/// <summary>Which world executes the handler.</summary>
	public required CommandLocation RunsIn { get; init; }

	/// <summary>Optional palette grouping, e.g. "View", "Terminal", "Diff".</summary>
	public string? Category { get; init; }

	/// <summary>Human- and Claude-facing description (longer than <see cref="Title"/>).</summary>
	public string Description { get; init; } = string.Empty;

	/// <summary>Natural-language hints that help Claude map intent onto this id (e.g. "reopen terminal").</summary>
	public IReadOnlyList<string> Aliases { get; init; } = [];

	/// <summary>Default keybinding(s); a user keybinding can override or unbind them.</summary>
	public IReadOnlyList<CommandKeybinding> DefaultKeybindings { get; init; } = [];

	/// <summary>Whether this command appears in the omnibar command palette (false for keybinding-only commands).</summary>
	public bool ShowInPalette { get; init; } = true;

	/// <summary>
	/// A context-key expression gating the command's keybinding activation and palette visibility (e.g.
	/// <c>terminalFocused &amp;&amp; !inputFocused</c>). It does <b>not</b> gate programmatic/MCP invocation.
	/// </summary>
	public string? When { get; init; }

	/// <summary>
	/// Optional raw-JSON Schema for the command's <c>args</c> object (the <c>properties</c> map), surfaced
	/// in <c>listCommands</c> so Claude knows the argument shape. Informational; coercion is lenient.
	/// </summary>
	public string? ArgsSchemaJson { get; init; }
}
