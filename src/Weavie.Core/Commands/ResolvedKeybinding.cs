namespace Weavie.Core.Commands;

/// <summary>
/// One effective key binding after merging command defaults with the user's
/// <c>~/.weavie/keybindings.json</c>. Shipped to the web (which resolves keydowns against it) and
/// surfaced in <c>listCommands</c>. The <see cref="Key"/> keeps the tinykeys-style <c>$mod</c> form;
/// the web formats it for display.
/// </summary>
public sealed record ResolvedKeybinding {
	/// <summary>The chord, e.g. <c>$mod+1</c>.</summary>
	public required string Key { get; init; }

	/// <summary>The registered command id this key invokes.</summary>
	public required string Command { get; init; }

	/// <summary>Optional raw-JSON argument object passed to the command, e.g. <c>{"index":1}</c>.</summary>
	public string? ArgsJson { get; init; }

	/// <summary>Optional context-key guard; the web only fires the binding when it evaluates true.</summary>
	public string? When { get; init; }

	/// <summary>
	/// When true, the host registers this as an OS-level global hotkey (fires even when Weavie is
	/// unfocused) and the web keydown resolver skips it. See <see cref="CommandKeybinding.Global"/>.
	/// </summary>
	public bool Global { get; init; }
}
