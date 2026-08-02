using Weavie.Core.Commands;

namespace Weavie.Linux.Hosting;

internal interface ILinuxGlobalHotkeyBackend : IDisposable {
	void Apply(IReadOnlyList<GlobalHotkey> hotkeys);

	event Action<GlobalHotkey, string?>? Pressed;

	event Action<string>? Log;
}
