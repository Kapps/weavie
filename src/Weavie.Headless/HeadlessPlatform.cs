using Weavie.Core.Commands;
using Weavie.Core.Shell;
using Weavie.Hosting;

namespace Weavie.Headless;

/// <summary>
/// The headless platform shell: a WebSocket bridge, no UI thread (inline dispatch), POSIX PTYs, and none of the
/// native window / hotkey / dialog capabilities. The thinnest <see cref="IHostPlatform"/> — the existence proof
/// that, once <see cref="HostCore"/> owns the logic, a host is just a transport plus these few facts.
/// </summary>
internal sealed class HeadlessPlatform : IHostPlatform {
	public HeadlessPlatform(IHostBridge bridge) {
		ArgumentNullException.ThrowIfNull(bridge);
		Bridge = bridge;
		Dispatcher = new InlineUiDispatcher();
		PtyLauncher = new PosixPtyLauncher();
	}

	public IHostBridge Bridge { get; }

	public IUiDispatcher Dispatcher { get; }

	public IPtyLauncher PtyLauncher { get; }

	// A browser has no native window chrome, so render Weavie's custom title bar (icon + File/View menus +
	// Omnibar). The window controls are cosmetic here (no OS window to drive); the value is the Omnibar.
	public string ChromePlatform => "web";

	public string? TitleBar => "custom";

	public IReadOnlyList<string> Recents => [];

	public IShellWindow? Window => null;

	public IGlobalHotkeyRegistrar? HotkeyRegistrar => null;

	public IHostDialogs? Dialogs => null;

	public void ToggleWindow() {
		// No OS window in a browser; the window-toggle command is a no-op here.
	}
}
