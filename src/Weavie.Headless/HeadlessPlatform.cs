using Weavie.Core.Mcp;
using Weavie.Core.Shell;
using Weavie.Hosting;

namespace Weavie.Headless;

/// <summary>
/// The thinnest <see cref="IHostPlatform"/>: WebSocket bridge, a serial dispatcher standing in for the UI
/// thread, per-OS PTY backend, no native window or dialog.
/// </summary>
internal sealed class HeadlessPlatform : IHostPlatform {
	public HeadlessPlatform(IWebTransportHub bridge, IUiDispatcher dispatcher, HostTransport transport) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(dispatcher);
		Bridge = bridge;
		Dispatcher = dispatcher;
		Transport = transport;
		PtyLauncher = OperatingSystem.IsWindows() ? new WindowsPtyLauncher() : new PosixPtyLauncher();
	}

	public IWebTransportHub Bridge { get; }

	public IUiDispatcher Dispatcher { get; }

	public IPtyLauncher PtyLauncher { get; }

	public HostTransport Transport { get; }

	public string ChromePlatform => "web";

	public IReadOnlyList<string> Recents => [];

	public event Action? RecentsChanged {
		add { }
		remove { }
	}

	// A browser page has the bar and menus but no window to move or buttons to draw.
	public IShellWindow Window { get; } = new FixedShellWindow(WindowChrome.Browser);

	public IShellMenuActions MenuActions => NoopShellMenuActions.Instance;

	public IApplicationMenu ApplicationMenu => NoopApplicationMenu.Instance;

	public IHostDialogs? Dialogs => null;

	public Weavie.Core.Sessions.ISystemNotificationChannel Notifications =>
		Weavie.Core.Sessions.NoopSystemNotificationChannel.Instance;

	public void ToggleWindow() {
		// No OS window in a browser; no-op.
	}

	public void ActivateWindow(string? activationToken) {
		// The browser owns its own window activation.
	}

	// The clipboard + browser belong to the remote browser, not this server process, so these are no-ops here
	// (a browser-side path is the remote-sessions story, deferred).
	public void WriteClipboard(string text) {
		// No host clipboard in a browser-served host.
	}

	public string ReadClipboard() => string.Empty;

	public ClipboardImage ReadClipboardImage() => ClipboardImage.None;

	public void OpenExternalUrl(string url) {
		// No host browser in a browser-served host.
	}
}
