using ObjCRuntime;
using Weavie.Core.Commands;

namespace Weavie.Mac.Hosting;

/// <summary>
/// Builds the macOS menu bar: File/View menus plus the standard App/Edit/Window menus. File/View items dispatch
/// the same Weavie command ids the keybindings and palette use, with shortcuts read from the keybinding store
/// (never hardcoded) so a rebind keeps the menu in sync. App/Edit/Window use the platform's own conventions.
/// </summary>
internal static class MacAppMenu {
	/// <summary>
	/// Builds the whole menu bar. <paramref name="resolveChord"/> returns a command id's effective chord (so the
	/// menu shows + binds its shortcut); <paramref name="recents"/> seeds File ▸ Open Recent.
	/// </summary>
	public static NSMenu Build(
		Action<string> runCommand,
		Func<string, string?> resolveChord,
		Action openFolder,
		Action<string> openRecent,
		IReadOnlyList<string> recents) {
		ArgumentNullException.ThrowIfNull(runCommand);
		ArgumentNullException.ThrowIfNull(resolveChord);
		ArgumentNullException.ThrowIfNull(openFolder);
		ArgumentNullException.ThrowIfNull(openRecent);
		ArgumentNullException.ThrowIfNull(recents);

		var menuBar = new NSMenu();
		menuBar.AddItem(BuildAppMenu());
		menuBar.AddItem(BuildFileMenu(runCommand, resolveChord, openFolder, openRecent, recents));
		menuBar.AddItem(BuildEditMenu());
		menuBar.AddItem(BuildViewMenu(runCommand, resolveChord));
		menuBar.AddItem(BuildWindowMenu());
		return menuBar;
	}

	private static NSMenuItem BuildAppMenu() {
		var app = NSApplication.SharedApplication;
		var menu = new NSMenu("weavie");
		menu.AddItem(new NSMenuItem("About weavie", (_, _) => app.OrderFrontStandardAboutPanel(app)));
		menu.AddItem(NSMenuItem.SeparatorItem);
		menu.AddItem(new NSMenuItem("Hide weavie", "h", (_, _) => app.Hide(app)));
		menu.AddItem(new NSMenuItem("Hide Others", "h", (_, _) => app.HideOtherApplications(app)) {
			KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.AlternateKeyMask,
		});
		menu.AddItem(new NSMenuItem("Show All", (_, _) => app.UnhideAllApplications(app)));
		menu.AddItem(NSMenuItem.SeparatorItem);
		menu.AddItem(new NSMenuItem("Quit weavie", "q", (_, _) => app.Terminate(app)));
		return Submenu("weavie", menu);
	}

	private static NSMenuItem BuildFileMenu(
		Action<string> runCommand,
		Func<string, string?> resolveChord,
		Action openFolder,
		Action<string> openRecent,
		IReadOnlyList<string> recents) {
		var menu = new NSMenu("File");
		menu.AddItem(CommandItem("New File", CoreCommands.NewFile, runCommand, resolveChord));
		menu.AddItem(NSMenuItem.SeparatorItem);
		menu.AddItem(new NSMenuItem("Open Folder…", "o", (_, _) => openFolder()));
		menu.AddItem(BuildOpenRecentItem(openRecent, recents));
		menu.AddItem(NSMenuItem.SeparatorItem);
		menu.AddItem(CommandItem("Save", CoreCommands.SaveFile, runCommand, resolveChord));
		menu.AddItem(NSMenuItem.SeparatorItem);
		// ⌘W closes the front window (standard macOS Close), distinct from ⌘Q (Quit).
		menu.AddItem(new NSMenuItem("Close Window", "w", (_, _) => NSApplication.SharedApplication.KeyWindow?.PerformClose(null)));
		return Submenu("File", menu);
	}

	private static NSMenuItem BuildOpenRecentItem(Action<string> openRecent, IReadOnlyList<string> recents) {
		var submenu = new NSMenu("Open Recent");
		if (recents.Count == 0) {
			// An action-less item is auto-disabled (NSMenu.AutoEnablesItems), so it reads as a greyed hint.
			submenu.AddItem(new NSMenuItem("No Recent Folders"));
		} else {
			foreach (string path in recents) {
				submenu.AddItem(new NSMenuItem(Leaf(path), (_, _) => openRecent(path)) { ToolTip = path });
			}
		}

		return Submenu("Open Recent", submenu);
	}

	private static NSMenuItem BuildEditMenu() {
		// Standard editing actions routed to the first responder (focused WKWebView / Monaco / text field)
		// via the platform selectors, so cut/copy/paste/undo work in the editor and every input.
		var menu = new NSMenu("Edit");
		menu.AddItem(new NSMenuItem("Undo", new Selector("undo:"), "z"));
		menu.AddItem(new NSMenuItem("Redo", new Selector("redo:"), "z") {
			KeyEquivalentModifierMask = NSEventModifierMask.CommandKeyMask | NSEventModifierMask.ShiftKeyMask,
		});
		menu.AddItem(NSMenuItem.SeparatorItem);
		menu.AddItem(new NSMenuItem("Cut", new Selector("cut:"), "x"));
		menu.AddItem(new NSMenuItem("Copy", new Selector("copy:"), "c"));
		menu.AddItem(new NSMenuItem("Paste", new Selector("paste:"), "v"));
		menu.AddItem(new NSMenuItem("Select All", new Selector("selectAll:"), "a"));
		return Submenu("Edit", menu);
	}

	private static NSMenuItem BuildViewMenu(Action<string> runCommand, Func<string, string?> resolveChord) {
		var menu = new NSMenu("View");
		menu.AddItem(CommandItem("Command Palette…", CoreCommands.FocusOmnibarCommands, runCommand, resolveChord));
		menu.AddItem(CommandItem("Go to File…", CoreCommands.FocusOmnibarFiles, runCommand, resolveChord));
		menu.AddItem(NSMenuItem.SeparatorItem);
		menu.AddItem(CommandItem("Toggle Files", CoreCommands.ToggleFileBrowser, runCommand, resolveChord));
		menu.AddItem(NSMenuItem.SeparatorItem);
		menu.AddItem(CommandItem("Reopen Terminal", CoreCommands.ReopenTerminal, runCommand, resolveChord));
		return Submenu("View", menu);
	}

	private static NSMenuItem BuildWindowMenu() {
		var menu = new NSMenu("Window");
		menu.AddItem(new NSMenuItem("Minimize", "m", (_, _) => NSApplication.SharedApplication.KeyWindow?.Miniaturize(null)));
		menu.AddItem(new NSMenuItem("Zoom", (_, _) => NSApplication.SharedApplication.KeyWindow?.PerformZoom(null)));
		var item = Submenu("Window", menu);
		// Let macOS own the Window menu (it adds the window list + "Bring All to Front").
		NSApplication.SharedApplication.WindowsMenu = menu;
		return item;
	}

	/// <summary>
	/// A menu item that dispatches a Weavie command id, binding its effective shortcut when the chord maps to a
	/// single-character menu key equivalent.
	/// </summary>
	private static NSMenuItem CommandItem(
		string title, string commandId, Action<string> runCommand, Func<string, string?> resolveChord) {
		string? chord = resolveChord(commandId);
		if (chord is not null && TryNativeShortcut(chord, out string keyEquivalent, out var mask)) {
			return new NSMenuItem(title, keyEquivalent, (_, _) => runCommand(commandId)) {
				KeyEquivalentModifierMask = mask,
			};
		}

		return new NSMenuItem(title, (_, _) => runCommand(commandId));
	}

	/// <summary>
	/// Maps a tinykeys-style chord (e.g. <c>$mod+shift+p</c>) to an NSMenuItem key equivalent + mask. Only
	/// single-character keys map; named keys or multi-key sequences return false (item shows no shortcut).
	/// </summary>
	private static bool TryNativeShortcut(string chord, out string keyEquivalent, out NSEventModifierMask mask) {
		keyEquivalent = string.Empty;
		mask = 0;
		var parsed = ChordParser.Parse(chord);
		if (!parsed.HasKey || parsed.Key.Length != 1) {
			return false;
		}

		keyEquivalent = parsed.Key; // already lowercased, so an uppercase letter never implies an extra Shift

		// $mod and Meta both resolve to Command on macOS.
		if (parsed.Modifiers.HasFlag(HotkeyModifiers.Mod) || parsed.Modifiers.HasFlag(HotkeyModifiers.Meta)) {
			mask |= NSEventModifierMask.CommandKeyMask;
		}

		if (parsed.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) {
			mask |= NSEventModifierMask.ControlKeyMask;
		}

		if (parsed.Modifiers.HasFlag(HotkeyModifiers.Shift)) {
			mask |= NSEventModifierMask.ShiftKeyMask;
		}

		if (parsed.Modifiers.HasFlag(HotkeyModifiers.Alt)) {
			mask |= NSEventModifierMask.AlternateKeyMask;
		}

		return true;
	}

	private static NSMenuItem Submenu(string title, NSMenu submenu) {
		var item = new NSMenuItem(title);
		item.Submenu = submenu;
		return item;
	}

	/// <summary>The folder's leaf name for the Open Recent label (e.g. <c>weavie</c> for <c>/src/weavie</c>).</summary>
	private static string Leaf(string path) {
		string leaf = Path.GetFileName(path.TrimEnd('/'));
		return string.IsNullOrEmpty(leaf) ? path : leaf;
	}
}
