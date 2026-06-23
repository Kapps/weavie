using Weavie.Core.Commands;
using Weavie.Core.Shell;
using Weavie.Hosting;
using Weavie.Mac.Hosting;

namespace Weavie.Mac;

// The macOS IHostPlatform seam. The app delegate already owns the native pieces, so it implements it directly;
// explicit interface members keep them off its public API.
public sealed partial class AppDelegate : IHostPlatform {
	IHostBridge IHostPlatform.Bridge => _bridge;

	// Async, never synchronous: a sync hop from the PTY read thread can deadlock a main-thread PTY write (see HostBridge).
	IUiDispatcher IHostPlatform.Dispatcher => _dispatcher!;

	IPtyLauncher IHostPlatform.PtyLauncher => _ptyLauncher;

	string IHostPlatform.ChromePlatform => "mac";

	// Native NSWindow chrome plus the web omnibar strip (no web window controls).
	string? IHostPlatform.TitleBar => "mac";

	IReadOnlyList<string> IHostPlatform.Recents => _recents?.Items ?? [];

	// Native NSWindow chrome + NSMenu, so no web title bar drives the window.
	IShellWindow? IHostPlatform.Window => null;

	IGlobalHotkeyRegistrar? IHostPlatform.HotkeyRegistrar => _hotkeyRegistrar;

	IHostDialogs? IHostPlatform.Dialogs => _dialogs;

	void IHostPlatform.ToggleWindow() => ToggleWindow();
}
