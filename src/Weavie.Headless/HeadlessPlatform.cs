using Weavie.Core.Commands;
using Weavie.Core.Shell;
using Weavie.Hosting;

namespace Weavie.Headless;

/// <summary>
/// The thinnest <see cref="IHostPlatform"/>: WebSocket bridge, a serial dispatcher standing in for the UI
/// thread, per-OS PTY backend, no native window / hotkey / dialog.
/// </summary>
internal sealed class HeadlessPlatform : IHostPlatform {
	public HeadlessPlatform(IHostBridge bridge, IUiDispatcher dispatcher) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(dispatcher);
		Bridge = bridge;
		Dispatcher = dispatcher;
		PtyLauncher = OperatingSystem.IsWindows() ? new WindowsPtyLauncher() : new PosixPtyLauncher();
	}

	public IHostBridge Bridge { get; }

	public IUiDispatcher Dispatcher { get; }

	public IPtyLauncher PtyLauncher { get; }

	// A browser has no native chrome, so render Weavie's custom title bar.
	public string ChromePlatform => "web";

	public string? TitleBar => "custom";

	public IReadOnlyList<string> Recents => [];

	public IShellWindow? Window => null;

	public IGlobalHotkeyRegistrar? HotkeyRegistrar => null;

	public IHostDialogs? Dialogs => null;

	public void ToggleWindow() {
		// No OS window in a browser; no-op.
	}

	// The clipboard + browser belong to the remote browser, not this server process, so these are no-ops here
	// (a browser-side path is the remote-sessions story, deferred).
	public void WriteClipboard(string text) {
		// No host clipboard in a browser-served host.
	}

	public string ReadClipboard() => string.Empty;

	public void OpenExternalUrl(string url) {
		// No host browser in a browser-served host.
	}
}
