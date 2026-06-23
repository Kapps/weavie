using System.Diagnostics;
using System.Runtime.InteropServices;
using Weavie.Core.Commands;
using Weavie.Core.Shell;
using Weavie.Core.Workspaces;
using Weavie.Hosting;
using Weavie.Linux.Native;

namespace Weavie.Linux.Hosting;

/// <summary>
/// The GTK + WebKitGTK platform shell: bridge, GLib-main-loop UI marshal, and POSIX PTYs. Unwired optional
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

	// Called on the GTK main thread from OnWebMessage, where the clipboard API is valid. Store so the text
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
