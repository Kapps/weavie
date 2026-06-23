namespace Weavie.Core.Commands;

/// <summary>The modifier set of a parsed chord. <see cref="Mod"/> is left unresolved so the per-OS
/// registrar maps it (Cmd on macOS, Ctrl elsewhere).</summary>
[Flags]
public enum HotkeyModifiers {
	/// <summary>No modifiers.</summary>
	None = 0,

	/// <summary>The Control key.</summary>
	Ctrl = 1 << 0,

	/// <summary>The Shift key.</summary>
	Shift = 1 << 1,

	/// <summary>The Alt / Option key.</summary>
	Alt = 1 << 2,

	/// <summary>The Meta / Command / Windows / Super key.</summary>
	Meta = 1 << 3,

	/// <summary>The platform <c>$mod</c> token — Cmd on macOS, Ctrl elsewhere — resolved by the registrar.</summary>
	Mod = 1 << 4,
}

/// <summary>A chord split into its modifier set and its single non-modifier key token (lowercased).</summary>
public readonly record struct ParsedChord(HotkeyModifiers Modifiers, string Key) {
	/// <summary>Whether the chord carries a non-modifier key (a modifiers-only chord can't be a hotkey).</summary>
	public bool HasKey => Key.Length > 0;
}

/// <summary>
/// Parses a tinykeys-style chord (<c>ctrl+`</c>, <c>$mod+Shift+p</c>) into modifiers + a key token, mirroring
/// the web resolver's <c>parseChord</c> so Core and web agree. Single-chord only (no sequences).
/// </summary>
public static class ChordParser {
	/// <summary>Parses <paramref name="chord"/>; an empty/whitespace token list yields no key (an invalid hotkey).</summary>
	public static ParsedChord Parse(string chord) {
		var modifiers = HotkeyModifiers.None;
		string key = string.Empty;
		if (string.IsNullOrEmpty(chord)) {
			return new ParsedChord(modifiers, key);
		}

		foreach (string raw in chord.Split('+')) {
			string part = raw.Trim();
			if (part.Length == 0) {
				continue;
			}

			switch (part.ToLowerInvariant()) {
				case "$mod":
				case "mod":
					modifiers |= HotkeyModifiers.Mod;
					break;
				case "ctrl":
				case "control":
					modifiers |= HotkeyModifiers.Ctrl;
					break;
				case "shift":
					modifiers |= HotkeyModifiers.Shift;
					break;
				case "alt":
				case "option":
					modifiers |= HotkeyModifiers.Alt;
					break;
				case "meta":
				case "cmd":
				case "command":
				case "win":
				case "super":
					modifiers |= HotkeyModifiers.Meta;
					break;
				default:
					// The last non-modifier token wins (a chord has one key).
					key = part.ToLowerInvariant();
					break;
			}
		}

		return new ParsedChord(modifiers, key);
	}
}
