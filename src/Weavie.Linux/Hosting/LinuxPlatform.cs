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

	// Host-bus handlers enter the GTK main thread before reaching the clipboard, whose reads are async in
	// GTK 4; the nested loop gives the bus back the synchronous answer it expects.
	public void WriteClipboard(string text) =>
		Gdk.gdk_clipboard_set_text(Clipboard, text ?? string.Empty);

	public string ReadClipboard() {
		IntPtr clipboard = Clipboard;
		return MainLoopWait.For(
			callback => Gdk.gdk_clipboard_read_text_async(clipboard, IntPtr.Zero, callback, IntPtr.Zero),
			result => {
				IntPtr text = Gdk.gdk_clipboard_read_text_finish(clipboard, result, out IntPtr error);
				GLib.g_clear_error(ref error);
				if (text == IntPtr.Zero) {
					return string.Empty;
				}

				try {
					return Marshal.PtrToStringUTF8(text) ?? string.Empty;
				} finally {
					GLib.g_free(text);
				}
			});
	}

	public ClipboardImage ReadClipboardImage() {
		IntPtr clipboard = Clipboard;
		return MainLoopWait.For(
			callback => Gdk.gdk_clipboard_read_texture_async(clipboard, IntPtr.Zero, callback, IntPtr.Zero),
			result => {
				IntPtr texture = Gdk.gdk_clipboard_read_texture_finish(clipboard, result, out IntPtr error);
				GLib.g_clear_error(ref error);
				if (texture == IntPtr.Zero) {
					return ClipboardImage.None;
				}

				try {
					return EncodePng(texture);
				} finally {
					GLib.g_object_unref(texture);
				}
			});
	}

	private static IntPtr Clipboard => Gdk.gdk_display_get_clipboard(Gdk.gdk_display_get_default());

	private static ClipboardImage EncodePng(IntPtr texture) {
		IntPtr encoded = Gdk.gdk_texture_save_to_png_bytes(texture);
		if (encoded == IntPtr.Zero) {
			return ClipboardImage.None;
		}

		try {
			IntPtr data = GLib.g_bytes_get_data(encoded, out nuint size);
			byte[] png = new byte[size];
			Marshal.Copy(data, png, 0, (int)size);
			return new ClipboardImage("image/png", png);
		} finally {
			GLib.g_bytes_unref(encoded);
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
