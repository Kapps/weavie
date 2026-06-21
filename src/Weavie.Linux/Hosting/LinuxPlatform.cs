using Weavie.Core.Commands;
using Weavie.Core.Shell;
using Weavie.Core.Workspaces;
using Weavie.Hosting;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

/// <summary>
/// The GTK + WebKitGTK platform shell: the bridge, the GLib-main-loop UI marshal
/// (<see cref="GtkMain.Invoke"/>), and POSIX PTYs. The GTK window provides native chrome; unwired optional
/// capabilities (title bar, hotkeys, dialogs) are <c>null</c> and <see cref="HostCore"/> degrades them to no-ops.
/// </summary>
internal sealed class LinuxPlatform : IHostPlatform {
	private readonly RecentWorkspaces _recents;

	public LinuxPlatform(HostBridge bridge, RecentWorkspaces recents) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(recents);
		Bridge = bridge;
		_recents = recents;
		Dispatcher = new DelegateUiDispatcher(GtkMain.Invoke);
		PtyLauncher = new PosixPtyLauncher();
	}

	public IHostBridge Bridge { get; }

	public IUiDispatcher Dispatcher { get; }

	public IPtyLauncher PtyLauncher { get; }

	public string ChromePlatform => "linux";

	// Native GTK decorations, so the web renders no custom title bar.
	public string? TitleBar => null;

	public IReadOnlyList<string> Recents => _recents.Items;

	public IShellWindow? Window => null;

	public IGlobalHotkeyRegistrar? HotkeyRegistrar => null;

	public IHostDialogs? Dialogs => null;

	public void ToggleWindow() {
		// No window toggle on the GTK host.
	}
}
