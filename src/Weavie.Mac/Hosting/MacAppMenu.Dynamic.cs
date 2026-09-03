using Weavie.Core.Commands;
using Weavie.Hosting;

namespace Weavie.Mac.Hosting;

internal sealed partial class MacAppMenu {
	private static NSMenuItem BuildDynamicMenu(
		ApplicationMenuDefinition definition,
		long revision,
		MacAppMenuChannel channel) {
		var menu = DynamicMenu(definition.Label);
		foreach (var entry in definition.Entries) {
			menu.AddItem(BuildDynamicEntry(entry, revision, channel));
		}
		return Submenu(definition.Label, menu);
	}

	private static NSMenuItem BuildDynamicEntry(
		ApplicationMenuEntry entry,
		long revision,
		MacAppMenuChannel channel) => entry.Kind switch {
			ApplicationMenuEntryKind.Command => BuildCommand(entry, revision, channel),
			ApplicationMenuEntryKind.Separator => NSMenuItem.SeparatorItem,
			ApplicationMenuEntryKind.Submenu => BuildSubmenu(entry, revision, channel),
			_ => throw new InvalidOperationException($"Unknown application-menu entry kind '{entry.Kind}'."),
		};

	private static NSMenuItem BuildCommand(
		ApplicationMenuEntry entry,
		long revision,
		MacAppMenuChannel channel) {
		var item = new NSMenuItem(
			entry.Label,
			(_, _) => channel.Raise(new ApplicationMenuActivation(revision, entry.Token))) {
			Enabled = entry.Enabled,
			ToolTip = entry.ToolTip,
		};
		ApplyFirstRepresentableKey(item, entry.Keys);
		return item;
	}

	private static NSMenuItem BuildSubmenu(
		ApplicationMenuEntry entry,
		long revision,
		MacAppMenuChannel channel) {
		var menu = DynamicMenu(entry.Label);
		foreach (var child in entry.Entries) {
			menu.AddItem(BuildDynamicEntry(child, revision, channel));
		}
		var item = Submenu(entry.Label, menu);
		item.Enabled = entry.Enabled;
		return item;
	}

	private static NSMenu DynamicMenu(string title) => new DisplayOnlyKeyEquivalentMenu(title) {
		AutoEnablesItems = false,
	};

	private static void ApplyFirstRepresentableKey(NSMenuItem item, IReadOnlyList<string> keys) {
		foreach (string key in keys) {
			var chord = ChordParser.Parse(key);
			if (!TryKeyEquivalent(chord.Key, out string equivalent)) {
				continue;
			}

			item.KeyEquivalent = equivalent;
			item.KeyEquivalentModifierMask = ModifierMask(chord.Modifiers);
			return;
		}
	}

	private static NSEventModifierMask ModifierMask(HotkeyModifiers modifiers) {
		var mask = (NSEventModifierMask)0;
		if (modifiers.HasFlag(HotkeyModifiers.Mod) || modifiers.HasFlag(HotkeyModifiers.Meta)) {
			mask |= NSEventModifierMask.CommandKeyMask;
		}
		if (modifiers.HasFlag(HotkeyModifiers.Ctrl)) {
			mask |= NSEventModifierMask.ControlKeyMask;
		}
		if (modifiers.HasFlag(HotkeyModifiers.Shift)) {
			mask |= NSEventModifierMask.ShiftKeyMask;
		}
		if (modifiers.HasFlag(HotkeyModifiers.Alt)) {
			mask |= NSEventModifierMask.AlternateKeyMask;
		}
		return mask;
	}

	private static bool TryKeyEquivalent(string key, out string equivalent) {
		equivalent = key switch {
			"up" => "\uF700",
			"down" => "\uF701",
			"left" => "\uF702",
			"right" => "\uF703",
			"backspace" => "\b",
			"delete" => "\u007f",
			"end" => "\uF72B",
			"enter" or "return" => "\r",
			"esc" or "escape" => "\u001b",
			"home" => "\uF729",
			"pagedown" => "\uF72D",
			"pageup" => "\uF72C",
			"space" => " ",
			"tab" => "\t",
			_ => string.Empty,
		};
		if (equivalent.Length > 0) {
			return true;
		}
		if (key.Length >= 2
			&& key[0] == 'f'
			&& int.TryParse(key.AsSpan(1), out int function)
			&& function is >= 1 and <= 35) {
			equivalent = char.ConvertFromUtf32(0xF704 + function - 1);
			return true;
		}
		if (key.Length == 1) {
			equivalent = key;
			return true;
		}
		return false;
	}

	// The page owns keyboard dispatch: a row's key equivalent is a label, so AppKit must never match it.
	private sealed class DisplayOnlyKeyEquivalentMenu : NSMenu {
		public DisplayOnlyKeyEquivalentMenu(string title) : base(title) { }

		public override bool PerformKeyEquivalent(NSEvent theEvent) => false;
	}
}
