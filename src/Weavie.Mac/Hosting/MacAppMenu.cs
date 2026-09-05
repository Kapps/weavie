using ObjCRuntime;
using Weavie.Hosting;

namespace Weavie.Mac.Hosting;

/// <summary>Owns the process-wide AppKit menu and swaps in the key workspace window's resolved command menus.</summary>
internal sealed partial class MacAppMenu {
	private MacAppMenuChannel? _active;

	public MacAppMenu() {
		Rebuild(null);
	}

	public NSMenu MainMenu { get; } = new();

	public MacAppMenuChannel CreateChannel() => new(this);

	internal void Activate(MacAppMenuChannel channel) {
		_active = channel;
		Rebuild(channel);
	}

	internal void Apply(MacAppMenuChannel channel) {
		if (ReferenceEquals(_active, channel)) {
			Rebuild(channel);
		}
	}

	internal void Close(MacAppMenuChannel channel) {
		if (!ReferenceEquals(_active, channel)) {
			return;
		}

		_active = null;
		Rebuild(null);
	}

	private void Rebuild(MacAppMenuChannel? channel) {
		MainMenu.RemoveAllItems();
		MainMenu.AddItem(BuildAppMenu());
		if (channel is not null && channel.State is { Menus.Count: > 0 } state) {
			for (int index = 0; index < state.Menus.Count; index++) {
				MainMenu.AddItem(BuildDynamicMenu(state.Menus[index], state.Revision, channel));
				if (index == 0) {
					MainMenu.AddItem(BuildEditMenu());
				}
			}
		} else {
			MainMenu.AddItem(BuildEditMenu());
		}
		MainMenu.AddItem(BuildWindowMenu());
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

	private static NSMenuItem Submenu(string title, NSMenu submenu) =>
		new(title) { Submenu = submenu };
}
