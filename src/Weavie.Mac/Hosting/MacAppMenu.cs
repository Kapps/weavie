using ObjCRuntime;

namespace Weavie.Mac.Hosting;

/// <summary>
/// Builds the macOS-owned App/Edit/Window menus. Weavie commands live in the shared web application menu so
/// every platform reads the active command catalog, context, and effective keybindings through one renderer.
/// </summary>
internal static class MacAppMenu {
	/// <summary>Builds the native menus whose behavior belongs to AppKit rather than to a Weavie command.</summary>
	public static NSMenu Build() {
		var menuBar = new NSMenu();
		menuBar.AddItem(BuildAppMenu());
		menuBar.AddItem(BuildEditMenu());
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

	private static NSMenuItem BuildEditMenu() {
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

	private static NSMenuItem BuildWindowMenu() {
		var menu = new NSMenu("Window");
		menu.AddItem(new NSMenuItem("Minimize", "m", (_, _) => NSApplication.SharedApplication.KeyWindow?.Miniaturize(null)));
		menu.AddItem(new NSMenuItem("Zoom", (_, _) => NSApplication.SharedApplication.KeyWindow?.PerformZoom(null)));
		var item = Submenu("Window", menu);
		NSApplication.SharedApplication.WindowsMenu = menu;
		return item;
	}

	private static NSMenuItem Submenu(string title, NSMenu submenu) {
		var item = new NSMenuItem(title) { Submenu = submenu };
		return item;
	}
}
