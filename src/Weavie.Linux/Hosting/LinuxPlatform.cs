using System.Diagnostics;
using System.Runtime.InteropServices;
using Weavie.Core.Mcp;
using Weavie.Core.Shell;
using Weavie.Core.Workspaces;
using Weavie.Hosting;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

/// <summary>
/// The GTK + WebKitGTK platform shell: bridge, GLib-main-loop UI marshal, POSIX PTYs, and the native actions
/// behind the web-rendered Linux app bar. Unsupported optional capabilities remain <c>null</c>.
/// </summary>
internal sealed class LinuxPlatform : IHostPlatform {
	private readonly RecentWorkspaces _recents;
	private readonly Action _toggleWindow;
	private readonly Action<string?> _activateWindow;

	public LinuxPlatform(
		HostBridge bridge,
		RecentWorkspaces recents,
		IShellMenuActions menuActions,
		Weavie.Core.Sessions.ISystemNotificationChannel notifications,
		Action toggleWindow,
		Action<string?> activateWindow) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(recents);
		ArgumentNullException.ThrowIfNull(menuActions);
		ArgumentNullException.ThrowIfNull(notifications);
		ArgumentNullException.ThrowIfNull(toggleWindow);
		ArgumentNullException.ThrowIfNull(activateWindow);
		Bridge = bridge;
		_recents = recents;
		MenuActions = menuActions;
		_toggleWindow = toggleWindow;
		_activateWindow = activateWindow;
		Notifications = notifications;
		Dispatcher = new DelegateUiDispatcher(GtkMain.Invoke);
		PtyLauncher = new PosixPtyLauncher();
	}

	public IWebTransportHub Bridge { get; }

	public IUiDispatcher Dispatcher { get; }

	public IPtyLauncher PtyLauncher { get; }

	public string ChromePlatform => "linux";

	public HostTransport Transport => HostTransport.Local;

	// Native GTK decorations plus the web app bar (menus + omnibar, no web window controls).
	public string? TitleBar => "linux";

	public IReadOnlyList<string> Recents => _recents.Items;

	public IShellWindow? Window => null;

	public IShellMenuActions MenuActions { get; }

	public IHostDialogs? Dialogs => null;

	public Weavie.Core.Sessions.ISystemNotificationChannel Notifications { get; }

	public void ToggleWindow() => _toggleWindow();

	public void ActivateWindow(string? activationToken) => _activateWindow(activationToken);

	// Host-bus handlers enter the GTK main thread before reaching the clipboard API. Store so the text
	// survives this process exiting (X11 clipboards otherwise vanish with their owner).
	public void WriteClipboard(string text) {
		IntPtr clipboard = Gtk.gtk_clipboard_get(Gtk.SelectionClipboard);
		Gtk.gtk_clipboard_set_text(clipboard, text ?? string.Empty, -1);
		Gtk.gtk_clipboard_store(clipboard);
	}

	public string ReadClipboard() {
		IntPtr clipboard = Gtk.gtk_clipboard_get(Gtk.SelectionClipboard);
		IntPtr text = Gtk.gtk_clipboard_wait_for_text(clipboard);
		if (text == IntPtr.Zero) {
			return string.Empty;
		}

		try {
			return Marshal.PtrToStringUTF8(text) ?? string.Empty;
		} finally {
			GLib.g_free(text);
		}
	}

	public ClipboardImage ReadClipboardImage() {
		IntPtr clipboard = Gtk.gtk_clipboard_get(Gtk.SelectionClipboard);
		IntPtr pixbuf = Gtk.gtk_clipboard_wait_for_image(clipboard);
		if (pixbuf == IntPtr.Zero) {
			return ClipboardImage.None;
		}

		try {
			return GdkPixbuf.EncodePng(pixbuf) is { } bytes ? new ClipboardImage("image/png", bytes) : ClipboardImage.None;
		} finally {
			GLib.g_object_unref(pixbuf);
		}
	}

	public void OpenExternalUrl(string url) {
		if (string.IsNullOrEmpty(url)) {
			return;
		}

		try {
			var info = new ProcessStartInfo { FileName = "xdg-open", UseShellExecute = false };
			info.ArgumentList.Add(url);
			Process.Start(info);
		} catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
			Console.Error.WriteLine($"[weavie] open-url failed: {ex.Message}");
		}
	}
}
