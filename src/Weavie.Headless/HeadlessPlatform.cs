using Weavie.Core.Commands;
using Weavie.Core.Shell;
using Weavie.Hosting;

namespace Weavie.Headless;

/// <summary>
/// The thinnest <see cref="IHostPlatform"/>: WebSocket bridge, inline dispatch, per-OS PTY backend, no
/// native window / hotkey / dialog.
/// </summary>
internal sealed class HeadlessPlatform : IHostPlatform {
	public HeadlessPlatform(IHostBridge bridge) {
		ArgumentNullException.ThrowIfNull(bridge);
		Bridge = bridge;
		Dispatcher = new InlineUiDispatcher();
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
}
