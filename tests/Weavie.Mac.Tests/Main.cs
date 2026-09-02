using AppKit;
using CoreGraphics;
using Weavie.Hosting;
using Weavie.Mac.Hosting;

NSApplication.Init();

var menu = new MacAppMenu();
using var first = menu.CreateChannel();
using var second = menu.CreateChannel();
var firstActivations = new List<ApplicationMenuActivation>();
var secondActivations = new List<ApplicationMenuActivation>();
((IApplicationMenu)first).Activated += firstActivations.Add;
((IApplicationMenu)second).Activated += secondActivations.Add;

((IApplicationMenu)first).Apply(State(1, "first"));
first.Activate();
Sequence(
	["weavie", "File", "Edit", "Go", "View", "Diff", "Run", "Window"],
	menu.MainMenu.Items.Select(item => item.Title).ToArray(),
	"main-menu order");
var staleFile = Submenu(menu.MainMenu.ItemAt(1), "first File menu");
var staleItem = Item(staleFile, 0, "first command");
Equal("k", staleItem.KeyEquivalent, "displayed key equivalent");
Equal(NSEventModifierMask.CommandKeyMask, staleItem.KeyEquivalentModifierMask, "displayed modifier");

((IApplicationMenu)second).Apply(State(2, "second"));
second.Activate();
staleFile.PerformActionForItem(0);
Sequence([new ApplicationMenuActivation(1, "first")], firstActivations, "stale item owner");
Sequence([], secondActivations, "active window must not receive stale item");

var currentFile = Submenu(menu.MainMenu.ItemAt(1), "second File menu");
using var keyEvent = NSEvent.KeyEvent(
	NSEventType.KeyDown,
	CGPoint.Empty,
	NSEventModifierMask.CommandKeyMask,
	0,
	0,
	null,
	"k",
	"k",
	false,
	40) ?? throw new InvalidOperationException("Could not create the test key event.");
False(currentFile.PerformKeyEquivalent(keyEvent), "dynamic key equivalent must remain display-only");
Sequence([], secondActivations, "display-only key equivalent activation");

currentFile.PerformActionForItem(0);
Sequence([new ApplicationMenuActivation(2, "second")], secondActivations, "mouse menu activation");
return 0;

static ApplicationMenuState State(long revision, string token) => new() {
	Revision = revision,
	Menus = [
		new ApplicationMenuDefinition {
			Label = "File",
			Entries = [
				new ApplicationMenuEntry {
					Kind = ApplicationMenuEntryKind.Command,
					Label = "Command",
					Enabled = true,
					Token = token,
					Keys = ["$mod+k"],
					Entries = [],
				},
			],
		},
		new ApplicationMenuDefinition { Label = "Go", Entries = [] },
		new ApplicationMenuDefinition { Label = "View", Entries = [] },
		new ApplicationMenuDefinition { Label = "Diff", Entries = [] },
		new ApplicationMenuDefinition { Label = "Run", Entries = [] },
	],
};

static NSMenuItem Item(NSMenu menu, nint index, string name) =>
	menu.ItemAt(index) ?? throw new InvalidOperationException($"Missing {name}.");

static NSMenu Submenu(NSMenuItem? item, string name) =>
	item?.Submenu ?? throw new InvalidOperationException($"Missing {name}.");

static void False(bool actual, string name) {
	if (actual) {
		throw new InvalidOperationException($"Expected false for {name}.");
	}
}

static void Sequence<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string name) {
	if (!expected.SequenceEqual(actual)) {
		throw new InvalidOperationException(
			$"Unexpected {name}: [{string.Join(", ", actual)}], expected [{string.Join(", ", expected)}].");
	}
}

static void Equal<T>(T expected, T actual, string name)
	where T : notnull {
	if (!EqualityComparer<T>.Default.Equals(expected, actual)) {
		throw new InvalidOperationException($"Unexpected {name}: {actual}, expected {expected}.");
	}
}
