using Weavie.Core.Commands;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class ApplicationHotkeysTests {
	[Fact]
	public void RegistersAndDispatchesThroughThePlatformRegistrar() {
		var commands = CoreCommands.CreateRegistry();
		string path = Path.Combine(Path.GetTempPath(), "weavie-app-hotkeys", Guid.NewGuid().ToString("N"), "keybindings.json");
		using var keybindings = new KeybindingStore(commands, path, enableWatcher: false);
		var registrar = new FakeRegistrar();
		int toggles = 0;
		var hotkeys = new ApplicationHotkeys(commands, keybindings, registrar, () => toggles++, _ => { });

		var toggle = Assert.Single(registrar.Applied, binding => binding.Command == CoreCommands.ToggleWindow);
		registrar.Press(toggle);

		Assert.Equal(1, toggles);
		hotkeys.Dispose();
		Assert.Equal(1, registrar.DisposeCount);
	}

	private sealed class FakeRegistrar : IGlobalHotkeyRegistrar {
		public IReadOnlyList<GlobalHotkey> Applied { get; private set; } = [];

		public int DisposeCount { get; private set; }

		public event Action<GlobalHotkey>? Pressed;

		public event Action<string>? Log;

		public void Apply(IReadOnlyList<GlobalHotkey> hotkeys) => Applied = hotkeys;

		public void Press(GlobalHotkey hotkey) => Pressed?.Invoke(hotkey);

		public void Dispose() {
			DisposeCount++;
			_ = Log;
		}
	}
}
