using Weavie.Core.Commands;
using Weavie.Core.Shell;
using Weavie.Hosting;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

/// <summary>
/// The GTK + WebKitGTK platform shell: the WebKitGTK bridge, the GLib-main-loop UI marshal
/// (<see cref="GtkMain.Invoke"/>), and POSIX PTYs. The GTK window provides native chrome, so there's no
/// web title bar, no global hotkeys, and no native file dialogs wired yet — those optional capabilities are
/// <c>null</c> and <see cref="HostCore"/> degrades them to no-ops.
/// </summary>
internal sealed class LinuxPlatform : IHostPlatform {
	public LinuxPlatform(HostBridge bridge) {
		ArgumentNullException.ThrowIfNull(bridge);
		Bridge = bridge;
		Dispatcher = new DelegateUiDispatcher(GtkMain.Invoke);
		PtyLauncher = new PosixPtyLauncher();
	}

	public IHostBridge Bridge { get; }

	public IUiDispatcher Dispatcher { get; }

	public IPtyLauncher PtyLauncher { get; }

	public string ChromePlatform => "linux";

	// Native GTK window decorations, so the web renders no custom title bar.
	public string? TitleBar => null;

	public IReadOnlyList<string> Recents => [];

	public IShellWindow? Window => null;

	public IGlobalHotkeyRegistrar? HotkeyRegistrar => null;

	public IHostDialogs? Dialogs => null;

	public void ToggleWindow() {
		// No global hotkey / toggle wired on the GTK host yet.
	}
}
