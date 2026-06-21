namespace Weavie.Core.Commands;

/// <summary>
/// A resolved global hotkey: a <see cref="ResolvedKeybinding.Global"/> binding with its chord parsed into a
/// modifier set + key token, ready for a per-OS registrar to register with the OS.
/// <see cref="Command"/>/<see cref="ArgsJson"/> are what <see cref="GlobalHotkeyService"/> invokes on a press.
/// </summary>
public sealed record GlobalHotkey {
	/// <summary>The command id this hotkey invokes.</summary>
	public required string Command { get; init; }

	/// <summary>Optional raw-JSON argument object passed to the command.</summary>
	public string? ArgsJson { get; init; }

	/// <summary>The raw chord string, e.g. <c>ctrl+`</c>, for logging/diagnostics.</summary>
	public required string Chord { get; init; }

	/// <summary>The parsed modifier set (<see cref="HotkeyModifiers.Mod"/> resolved by the registrar).</summary>
	public required HotkeyModifiers Modifiers { get; init; }

	/// <summary>The lowercased non-modifier key token, e.g. <c>`</c>, <c>f5</c>, <c>space</c>.</summary>
	public required string Key { get; init; }
}

/// <summary>
/// Registers OS-level global hotkeys — the per-platform seam behind <see cref="GlobalHotkeyService"/>.
/// Implementations (Windows <c>RegisterHotKey</c>, macOS Carbon <c>RegisterEventHotKey</c>) own the native
/// registration and report presses back; the Core service owns <em>which</em> hotkeys exist. Implementations
/// marshal to whatever thread the OS API requires; failures (an unmappable key, a chord another app owns)
/// surface via <see cref="Log"/> rather than a silent no-op.
/// </summary>
public interface IGlobalHotkeyRegistrar : IDisposable {
	/// <summary>
	/// Replaces the full set of OS-registered global hotkeys with <paramref name="hotkeys"/> (idempotent:
	/// unregister everything, then register each). Called when the resolved global bindings change.
	/// </summary>
	void Apply(IReadOnlyList<GlobalHotkey> hotkeys);

	/// <summary>Raised when a registered global hotkey is pressed.</summary>
	event Action<GlobalHotkey>? Pressed;

	/// <summary>Diagnostics: registration failures and unmappable keys.</summary>
	event Action<string>? Log;
}
