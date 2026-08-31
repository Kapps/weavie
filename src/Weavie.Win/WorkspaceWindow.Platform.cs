using System.ComponentModel;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Weavie.Core.Mcp;
using Weavie.Core.Shell;
using Weavie.Hosting;
using Weavie.Win.Hosting;

namespace Weavie.Win;

// The Windows IHostPlatform: the native surface HostCore reaches through. The workspace window owns the bridge, UI
// marshal, ConPTY launcher, dialogs, and web title bar, so it implements the seam via explicit members. Global
// hotkeys are app-level (AppController), outside this workspace-scoped adapter.
internal sealed partial class WorkspaceWindow {
	IWebTransportHub IHostPlatform.Bridge => _bridge;

	IUiDispatcher IHostPlatform.Dispatcher => _dispatcher;

	IPtyLauncher IHostPlatform.PtyLauncher => _ptyLauncher;

	string IHostPlatform.ChromePlatform => "win";

	HostTransport IHostPlatform.Transport => HostTransport.Local;

	string? IHostPlatform.TitleBar => "custom";

	IReadOnlyList<string> IHostPlatform.Recents => _app.Recents.Items;

	event Action? IHostPlatform.RecentsChanged {
		add => _app.Recents.Changed += value;
		remove => _app.Recents.Changed -= value;
	}

	IShellWindow? IHostPlatform.Window => this;

	IShellMenuActions IHostPlatform.MenuActions => this;

	IHostDialogs? IHostPlatform.Dialogs => _dialogs;

	Weavie.Core.Sessions.ISystemNotificationChannel IHostPlatform.Notifications => _notifications;

	void IHostPlatform.ToggleWindow() => WindowFocus.Toggle(this);

	void IHostPlatform.ActivateWindow(string? activationToken) => WindowFocus.ForceForeground(this);

	// Host-bus handlers enter the UI (STA) thread before reaching WinForms Clipboard. SetText rejects an
	// empty string, so an empty copy clears the clipboard instead.
	void IHostPlatform.WriteClipboard(string text) {
		try {
			if (string.IsNullOrEmpty(text)) {
				Clipboard.Clear();
			} else {
				Clipboard.SetText(text);
			}
		} catch (ExternalException ex) {
			Console.Error.WriteLine($"[weavie] clipboard write failed: {ex.Message}");
		}
	}

	string IHostPlatform.ReadClipboard() {
		try {
			return Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
		} catch (ExternalException ex) {
			Console.Error.WriteLine($"[weavie] clipboard read failed: {ex.Message}");
			return string.Empty;
		}
	}

	ClipboardImage IHostPlatform.ReadClipboardImage() {
		try {
			using var image = Clipboard.GetImage();
			if (image is null) {
				return ClipboardImage.None;
			}

			using var buffer = new MemoryStream();
			image.Save(buffer, ImageFormat.Png);
			return new ClipboardImage("image/png", buffer.ToArray());
		} catch (ExternalException ex) {
			Console.Error.WriteLine($"[weavie] clipboard image read failed: {ex.Message}");
			return ClipboardImage.None;
		}
	}

	void IHostPlatform.OpenExternalUrl(string url) {
		if (string.IsNullOrEmpty(url)) {
			return;
		}

		try {
			Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
		} catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ObjectDisposedException) {
			Console.Error.WriteLine($"[weavie] open-url failed: {ex.Message}");
		}
	}
}
