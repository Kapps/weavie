using System.Runtime.InteropServices;
using Weavie.Core.Commands;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

internal static class LinuxHotkeyMapping {
	internal static bool TryKeyval(string key, out uint keyval) {
		if (key.Length == 1) {
			keyval = Gdk.gdk_unicode_to_keyval(key[0]);
			return keyval != 0;
		}

		string name = key switch {
			"enter" or "return" => "Return",
			"tab" => "Tab",
			"esc" or "escape" => "Escape",
			"backspace" => "BackSpace",
			"del" or "delete" => "Delete",
			"insert" => "Insert",
			"home" => "Home",
			"end" => "End",
			"pageup" => "Page_Up",
			"pagedown" => "Page_Down",
			"up" => "Up",
			"down" => "Down",
			"left" => "Left",
			"right" => "Right",
			_ when key.Length > 1 && key[0] == 'f' => key.ToUpperInvariant(),
			_ => key,
		};
		keyval = Gdk.gdk_keyval_from_name(name);
		return keyval is not (0 or 0xffffff);
	}

	internal static bool TryPortalTrigger(GlobalHotkey hotkey, out string trigger) {
		if (!TryKeyval(hotkey.Key, out uint keyval)) {
			trigger = string.Empty;
			return false;
		}
		string? keyName = Marshal.PtrToStringUTF8(Gdk.gdk_keyval_name(keyval));
		if (string.IsNullOrEmpty(keyName)) {
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
}
