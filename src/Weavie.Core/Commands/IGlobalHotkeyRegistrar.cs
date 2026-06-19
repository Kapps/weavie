namespace Weavie.Core.Commands;

/// <summary>
/// A resolved global hotkey: a keybinding marked <see cref="ResolvedKeybinding.Global"/> whose chord has
/// been parsed into a modifier set + key token, ready for a per-OS registrar to register with the operating
/// system. <see cref="Command"/>/<see cref="ArgsJson"/> are what <see cref="GlobalHotkeyService"/> invokes
/// when the OS reports the hotkey pressed.
/// </summary>
public sealed record GlobalHotkey {
	/// <summary>The command id this hotkey invokes.</summary>
	public required string Command { get; init; }

	/// <summary>Optional raw-JSON argument object passed to the command.</summary>
	public string? ArgsJson { get; init; }

	/// <summary>The raw chord string, e.g. <c>ctrl+`</c> — kept for logging/diagnostics.</summary>
	public required string Chord { get; init; }

	/// <summary>The parsed modifier set (<see cref="HotkeyModifiers.Mod"/> resolved by the registrar).</summary>
	public required HotkeyModifiers Modifiers { get; init; }

	/// <summary>The lowercased non-modifier key token, e.g. <c>`</c>, <c>f5</c>, <c>space</c>.</summary>
	public required string Key { get; init; }
}

/// <summary>
/// Registers OS-level global hotkeys — the per-platform seam behind <see cref="GlobalHotkeyService"/>.
/// Implementations (Windows <c>RegisterHotKey</c>, macOS Carbon <c>RegisterEventHotKey</c>) own the native
/// registration and report presses back; the Core service owns <em>which</em> hotkeys exist (from the
/// resolved keybindings) and routes a press to the command dispatcher. Implementations marshal to whatever
/// thread the OS API requires; failures (an unmappable key, a chord another app already owns) surface via
/// <see cref="Log"/> — never a silent no-op.
/// </summary>
public interface IGlobalHotkeyRegistrar : IDisposable {
	/// <summary>
	/// Replaces the full set of OS-registered global hotkeys with <paramref name="hotkeys"/> (idempotent:
	/// unregister everything, then register each). Called whenever the resolved global bindings change.
	/// </summary>
	void Apply(IReadOnlyList<GlobalHotkey> hotkeys);

	/// <summary>Raised when a registered global hotkey is pressed (carries the <see cref="GlobalHotkey"/> that fired).</summary>
	event Action<GlobalHotkey>? Pressed;

	/// <summary>Loud diagnostics: registration failures and unmappable keys.</summary>
	event Action<string>? Log;
}
