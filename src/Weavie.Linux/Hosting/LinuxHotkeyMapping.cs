using Weavie.Core.Commands;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

internal static class LinuxHotkeyMapping {
	private static readonly IReadOnlyDictionary<string, string> KeyNames = new Dictionary<string, string>(StringComparer.Ordinal) {
		["space"] = "space",
		["enter"] = "Return",
		["return"] = "Return",
		["tab"] = "Tab",
		["escape"] = "Escape",
		["esc"] = "Escape",
		["backspace"] = "BackSpace",
		["delete"] = "Delete",
		["del"] = "Delete",
		["insert"] = "Insert",
		["home"] = "Home",
		["end"] = "End",
		["pageup"] = "Page_Up",
		["pagedown"] = "Page_Down",
		["up"] = "Up",
		["down"] = "Down",
		["left"] = "Left",
		["right"] = "Right",
		["`"] = "grave",
		["-"] = "minus",
		["="] = "equal",
		["["] = "bracketleft",
		["]"] = "bracketright",
		["\\"] = "backslash",
		[";"] = "semicolon",
		["'"] = "apostrophe",
		[","] = "comma",
		["."] = "period",
		["/"] = "slash",
	};

	internal static bool TryGetKeyName(string key, out string name) {
		if (KeyNames.TryGetValue(key, out string? named)) {
			name = named;
			return true;
		}

		if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) {
			name = key;
			return true;
		}

		if (key.Length >= 2
			&& key[0] == 'f'
			&& int.TryParse(key.AsSpan(1), out int function)
			&& function is >= 1 and <= 35) {
			name = $"F{function}";
			return true;
		}

		name = string.Empty;
		return false;
	}

	internal static uint X11Modifiers(HotkeyModifiers modifiers) {
		uint result = 0;
		if (modifiers.HasFlag(HotkeyModifiers.Ctrl) || modifiers.HasFlag(HotkeyModifiers.Mod)) {
			result |= X11.ControlMask;
		}
		if (modifiers.HasFlag(HotkeyModifiers.Shift)) {
			result |= X11.ShiftMask;
		}
		if (modifiers.HasFlag(HotkeyModifiers.Alt)) {
			result |= X11.Mod1Mask;
		}
		if (modifiers.HasFlag(HotkeyModifiers.Meta)) {
			result |= X11.Mod4Mask;
		}
		return result;
	}

	internal static bool TryPortalTrigger(GlobalHotkey hotkey, out string trigger) {
		if (!TryGetKeyName(hotkey.Key, out string keyName)) {
			trigger = string.Empty;
			return false;
		}

		var parts = new List<string>();
		var modifiers = hotkey.Modifiers;
		if (modifiers.HasFlag(HotkeyModifiers.Ctrl) || modifiers.HasFlag(HotkeyModifiers.Mod)) {
			parts.Add("CTRL");
		}
		if (modifiers.HasFlag(HotkeyModifiers.Alt)) {
			parts.Add("ALT");
		}
		if (modifiers.HasFlag(HotkeyModifiers.Shift)) {
			parts.Add("SHIFT");
		}
		if (modifiers.HasFlag(HotkeyModifiers.Meta)) {
			parts.Add("LOGO");
		}
		parts.Add(keyName);
		trigger = string.Join('+', parts);
		return true;
	}
}
