using Weavie.Core.Commands;
using Weavie.Core.Shell;
using Weavie.Hosting;
using Weavie.Mac.Hosting;

namespace Weavie.Mac;

// The macOS IHostPlatform seam. The app delegate already owns the native pieces, so it implements it directly;
// explicit interface members keep them off its public API.
public sealed partial class AppDelegate : IHostPlatform {
	IHostBridge IHostPlatform.Bridge => _bridge;

	// Async, never synchronous: a sync hop from the PTY read thread can deadlock a main-thread PTY write (see HostBridge).
	IUiDispatcher IHostPlatform.Dispatcher => _dispatcher!;

	IPtyLauncher IHostPlatform.PtyLauncher => _ptyLauncher;

	string IHostPlatform.ChromePlatform => "mac";

	// Native NSWindow chrome plus the web omnibar strip (no web window controls).
	string? IHostPlatform.TitleBar => "mac";

	IReadOnlyList<string> IHostPlatform.Recents => _recents?.Items ?? [];

	// Native NSWindow chrome + NSMenu, so no web title bar drives the window.
	IShellWindow? IHostPlatform.Window => null;

	IGlobalHotkeyRegistrar? IHostPlatform.HotkeyRegistrar => _hotkeyRegistrar;

	IHostDialogs? IHostPlatform.Dialogs => _dialogs;

	void IHostPlatform.ToggleWindow() => ToggleWindow();

	// The general pasteboard's plain-text UTI; read + write must agree on it.
	private const string PasteboardTextType = "public.utf8-plain-text";

	// Called on the main thread from OnWebMessage, where NSPasteboard / NSWorkspace are valid.
	void IHostPlatform.WriteClipboard(string text) {
		var pasteboard = NSPasteboard.GeneralPasteboard;
		pasteboard.ClearContents();
		pasteboard.SetStringForType(text ?? string.Empty, PasteboardTextType);
	}

	string IHostPlatform.ReadClipboard() =>
		NSPasteboard.GeneralPasteboard.GetStringForType(PasteboardTextType) ?? string.Empty;

	void IHostPlatform.OpenExternalUrl(string url) {
		if (string.IsNullOrEmpty(url) || NSUrl.FromString(url) is not { } nsUrl) {
			return;
		}

		NSWorkspace.SharedWorkspace.OpenUrl(nsUrl);
	}
}
