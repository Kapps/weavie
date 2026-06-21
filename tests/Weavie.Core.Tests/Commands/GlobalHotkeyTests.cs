using Weavie.Core.Commands;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Cross-platform half of global hotkeys: <see cref="ChordParser"/> (chord → modifiers + key) and
/// <see cref="GlobalHotkeyService"/> (picks global bindings, hands them to the registrar, routes a press
/// to the dispatcher). Driven with a fake registrar; the per-OS registrars run against the real OS.
/// </summary>
public sealed class GlobalHotkeyTests {
	[Theory]
	[InlineData("ctrl+`", HotkeyModifiers.Ctrl, "`")]
	[InlineData("$mod+shift+p", HotkeyModifiers.Mod | HotkeyModifiers.Shift, "p")]
	[InlineData("alt+F5", HotkeyModifiers.Alt, "f5")]
	[InlineData("ctrl+shift+alt+space", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift | HotkeyModifiers.Alt, "space")]
	[InlineData("cmd+k", HotkeyModifiers.Meta, "k")]
	public void ChordParser_Parses(string chord, HotkeyModifiers modifiers, string key) {
		var parsed = ChordParser.Parse(chord);
		Assert.Equal(modifiers, parsed.Modifiers);
		Assert.Equal(key, parsed.Key);
		Assert.True(parsed.HasKey);
	}

	[Fact]
	public void ChordParser_ModifiersOnly_HasNoKey() {
		var parsed = ChordParser.Parse("ctrl+shift");
		Assert.Equal(HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, parsed.Modifiers);
		Assert.False(parsed.HasKey);
	}

	[Fact]
	public void Service_AppliesOnlyGlobalBindings() {
		var (store, dispatcher) = Build(out _);
		var fake = new FakeRegistrar();
		using var service = new GlobalHotkeyService(store, dispatcher, fake);

		// Only the global binding reaches the registrar.
		var applied = Assert.Single(fake.Applied);
		Assert.Equal("weavie.window.toggle", applied.Command);
		Assert.Equal(HotkeyModifiers.Ctrl, applied.Modifiers);
		Assert.Equal("`", applied.Key);
		Assert.Equal("ctrl+`", applied.Chord);
	}

	[Fact]
	public void Service_RoutesPressToDispatcher() {
		var (store, dispatcher) = Build(out var invoked);
		var fake = new FakeRegistrar();
		using var service = new GlobalHotkeyService(store, dispatcher, fake);

		fake.Press(fake.Applied[0]);

		// Synchronous handler resolves before Press returns.
		Assert.Single(invoked);
	}

	// One global command + one non-global, a defaults-only store, and a dispatcher whose handler bumps a
	// counter. `invoked` reports how many times it ran.
	private static (KeybindingStore Store, CommandDispatcher Dispatcher) Build(out List<string> invoked) {
		var registry = new CommandRegistry();
		registry.Register(new CommandDefinition {
			Id = "weavie.window.toggle",
			Title = "Focus Window",
			RunsIn = CommandLocation.Core,
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+`", Global = true }],
		});
		registry.Register(new CommandDefinition {
			Id = "weavie.terminal.reopen",
			Title = "Reopen Terminal",
			RunsIn = CommandLocation.Core,
			DefaultKeybindings = [new CommandKeybinding { Key = "ctrl+shift+t" }], // not global
		});

		// Defaults only: nonexistent path, watcher off.
		string path = Path.Combine(Path.GetTempPath(), "weavie-hotkey-tests", Guid.NewGuid().ToString("N"), "keybindings.json");
		var store = new KeybindingStore(registry, path, enableWatcher: false);

		var calls = new List<string>();
		invoked = calls;
		var dispatcher = new CommandDispatcher(registry);
		dispatcher.RegisterHandler("weavie.window.toggle", (_, _) => {
			calls.Add("focus");
			return Task.FromResult(CommandResult.Success());
		});
		return (store, dispatcher);
	}

	private sealed class FakeRegistrar : IGlobalHotkeyRegistrar {
		public IReadOnlyList<GlobalHotkey> Applied { get; private set; } = [];

		public event Action<GlobalHotkey>? Pressed;

		public event Action<string>? Log;

		public void Apply(IReadOnlyList<GlobalHotkey> hotkeys) => Applied = [.. hotkeys];

		public void Press(GlobalHotkey hotkey) => Pressed?.Invoke(hotkey);

		public void Dispose() => _ = Log; // touch Log to suppress the unused-event warning
	}
}
