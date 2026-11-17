using System.Diagnostics;
using System.Runtime.InteropServices;
using Weavie.Core.Mcp;
using Weavie.Core.Shell;
using Weavie.Core.Workspaces;
using Weavie.Hosting;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

/// <summary>The one GTK window a Linux host runs in: its title bar, its File-menu actions, and its visibility.</summary>
internal interface ILinuxWindow : IShellWindow, IShellMenuActions {
	/// <summary>Hides the window when it is active, else shows it — the <c>weavie.window.toggle</c> action.</summary>
	void ToggleVisibility();

	/// <summary>Shows and raises the window, handing the compositor <paramref name="activationToken"/> when one came with the request.</summary>
	void Activate(string? activationToken);
}

/// <summary>
/// The GTK + WebKitGTK platform shell: bridge, GLib-main-loop UI marshal, POSIX PTYs, and the window whose
/// web-rendered title bar replaces the GTK header. Unsupported optional capabilities remain <c>null</c>.
/// </summary>
internal sealed class LinuxPlatform : IHostPlatform {
	private readonly RecentWorkspaces _recents;
	private readonly ILinuxWindow _window;

	public LinuxPlatform(
		HostBridge bridge,
		RecentWorkspaces recents,
		ILinuxWindow window,
		Weavie.Core.Sessions.ISystemNotificationChannel notifications) {
		ArgumentNullException.ThrowIfNull(bridge);
		ArgumentNullException.ThrowIfNull(recents);
		ArgumentNullException.ThrowIfNull(window);
		ArgumentNullException.ThrowIfNull(notifications);
		Bridge = bridge;
		_recents = recents;
		_window = window;
		Notifications = notifications;
		Dispatcher = new DelegateUiDispatcher(GtkMain.Invoke);
		PtyLauncher = new PosixPtyLauncher();
	}

	public IWebTransportHub Bridge { get; }

	public IUiDispatcher Dispatcher { get; }

	public IPtyLauncher PtyLauncher { get; }

	public string ChromePlatform => "linux";

	public HostTransport Transport => HostTransport.Local;

	public IReadOnlyList<string> Recents => _recents.Items;

	public event Action? RecentsChanged {
		add => _recents.Changed += value;
		remove => _recents.Changed -= value;
	}

	public IShellWindow Window => _window;

	public IShellMenuActions MenuActions => _window;

	public IApplicationMenu ApplicationMenu => NoopApplicationMenu.Instance;

	public IHostDialogs? Dialogs => null;

	public Weavie.Core.Sessions.ISystemNotificationChannel Notifications { get; }

	public void ToggleWindow() => _window.ToggleVisibility();

	public void ActivateWindow(string? activationToken) => _window.Activate(activationToken);

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
