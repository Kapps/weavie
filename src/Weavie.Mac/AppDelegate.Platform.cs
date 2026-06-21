using Weavie.Core.Commands;
using Weavie.Core.Shell;
using Weavie.Hosting;
using Weavie.Mac.Hosting;

namespace Weavie.Mac;

// The macOS IHostPlatform: the native surface HostCore reaches through. The app delegate owns the window,
// recents, hotkey registrar, and dialogs, so it implements the seam directly (explicit interface members
// keep them off its public API).
public sealed partial class AppDelegate : IHostPlatform {
	IHostBridge IHostPlatform.Bridge => _bridge;

	// Async (BeginInvokeOnMainThread), never synchronous: a sync hop from the PTY read thread can deadlock
	// against a main-thread PTY write (see HostBridge). Inlines when already on the main thread.
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
