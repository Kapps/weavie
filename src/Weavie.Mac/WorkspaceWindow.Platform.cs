using Weavie.Core.Commands;
using Weavie.Core.Mcp;
using Weavie.Core.Shell;
using Weavie.Hosting;

namespace Weavie.Mac;

// The macOS IHostPlatform seam, one per workspace window. The window owns the bridge + web view; the shared native
// pieces (UI marshal, PTY launcher, dialogs, recents) come from the controller. Native NSWindow chrome + NSMenu, so
// there's no web title bar driving the window (Window null), and global hotkeys are app-level (HotkeyRegistrar null).
internal sealed partial class WorkspaceWindow : IHostPlatform {
	IHostBridge IHostPlatform.Bridge => _bridge;

	// Async, never synchronous: a sync hop from the PTY read thread can deadlock a main-thread PTY write (see HostBridge).
	IUiDispatcher IHostPlatform.Dispatcher => _app.Dispatcher;

	IPtyLauncher IHostPlatform.PtyLauncher => _app.PtyLauncher;

	string IHostPlatform.ChromePlatform => "mac";

	HostTransport IHostPlatform.Transport => HostTransport.Local;

	// Native NSWindow chrome plus the web omnibar strip (no web window controls).
	string? IHostPlatform.TitleBar => "mac";

	IReadOnlyList<string> IHostPlatform.Recents => _app.Recents.Items;

	// Native NSWindow chrome + NSMenu, so no web title bar drives the window.
	IShellWindow? IHostPlatform.Window => null;

	// Global hotkeys are registered once at the app level (AppDelegate), not per window.
	IGlobalHotkeyRegistrar? IHostPlatform.HotkeyRegistrar => null;

	IHostDialogs? IHostPlatform.Dialogs => _app.Dialogs;

	void IHostPlatform.ToggleWindow() => _app.ToggleWindow(Window);

	// The general pasteboard's plain-text UTI; read + write must agree on it.
	private const string PasteboardTextType = "public.utf8-plain-text";

	// Image UTIs read on a claude-pane paste. A screenshot or Preview copy lands as TIFF; re-encode it to the PNG
	// claude ingests.
	private const string PasteboardPngType = "public.png";
	private const string PasteboardTiffType = "public.tiff";

	// Called on the main thread from OnWebMessage, where NSPasteboard / NSWorkspace are valid.
	void IHostPlatform.WriteClipboard(string text) {
		var pasteboard = NSPasteboard.GeneralPasteboard;
		pasteboard.ClearContents();
		pasteboard.SetStringForType(text ?? string.Empty, PasteboardTextType);
	}

	string IHostPlatform.ReadClipboard() =>
		NSPasteboard.GeneralPasteboard.GetStringForType(PasteboardTextType) ?? string.Empty;

	ClipboardImage IHostPlatform.ReadClipboardImage() {
		var pasteboard = NSPasteboard.GeneralPasteboard;
		var png = pasteboard.GetDataForType(PasteboardPngType);
		if (png is null && pasteboard.GetDataForType(PasteboardTiffType) is { } tiff) {
			using var rep = new NSBitmapImageRep(tiff);
			png = rep.RepresentationUsingTypeProperties(NSBitmapImageFileType.Png, new NSDictionary());
		}

		return png is null ? ClipboardImage.None : new ClipboardImage("image/png", [.. png]);
	}

	void IHostPlatform.OpenExternalUrl(string url) {
		if (string.IsNullOrEmpty(url) || NSUrl.FromString(url) is not { } nsUrl) {
			return;
		}

		NSWorkspace.SharedWorkspace.OpenUrl(nsUrl);
	}
}
