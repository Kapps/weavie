using Weavie.Core.Commands;
using Weavie.Core.Shell;
using Weavie.Hosting;

namespace Weavie.Headless;

/// <summary>
/// The headless platform shell: a WebSocket bridge, inline dispatch (no UI thread), POSIX PTYs, and no native
/// window / hotkey / dialog capabilities. The thinnest <see cref="IHostPlatform"/>.
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

	// A browser has no native chrome, so render Weavie's custom title bar (icon + File/View menus + Omnibar).
	// Window controls are cosmetic here (no OS window); the value is the Omnibar.
	public string ChromePlatform => "web";

	public string? TitleBar => "custom";

	public IReadOnlyList<string> Recents => [];

	public IShellWindow? Window => null;

	public IGlobalHotkeyRegistrar? HotkeyRegistrar => null;

	public IHostDialogs? Dialogs => null;

	public void ToggleWindow() {
		// No OS window in a browser; no-op.
	}
}
