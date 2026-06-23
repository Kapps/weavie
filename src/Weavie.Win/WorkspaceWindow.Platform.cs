using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Weavie.Core.Commands;
using Weavie.Core.Shell;
using Weavie.Hosting;
using Weavie.Win.Hosting;

namespace Weavie.Win;

// The Windows IHostPlatform: the native surface HostCore reaches through. The workspace window owns the bridge, UI
// marshal, ConPTY launcher, dialogs, and web title bar, so it implements the seam via explicit members. Global
// hotkeys are app-level (AppController), so HotkeyRegistrar is null here.
internal sealed partial class WorkspaceWindow {
	IHostBridge IHostPlatform.Bridge => _bridge;

	IUiDispatcher IHostPlatform.Dispatcher => _dispatcher;

	IPtyLauncher IHostPlatform.PtyLauncher => _ptyLauncher;

	string IHostPlatform.ChromePlatform => "win";

	string? IHostPlatform.TitleBar => "custom";

	IReadOnlyList<string> IHostPlatform.Recents => _app.Recents.Items;

	IShellWindow? IHostPlatform.Window => this;

	// Global hotkeys are registered once at the app level (AppController), not per window.
	IGlobalHotkeyRegistrar? IHostPlatform.HotkeyRegistrar => null;

	IHostDialogs? IHostPlatform.Dialogs => _dialogs;

	void IHostPlatform.ToggleWindow() => WindowFocus.Toggle(this);

	// Called on the UI (STA) thread from OnWebMessage, where WinForms Clipboard is valid. SetText rejects an
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
