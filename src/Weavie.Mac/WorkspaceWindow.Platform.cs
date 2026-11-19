using Weavie.Core.Mcp;
using Weavie.Core.Shell;
using Weavie.Hosting;

namespace Weavie.Mac;

// The macOS IHostPlatform seam, one per workspace window. The window owns the bridge + web view; the shared native
// pieces (UI marshal, PTY launcher, dialogs, recents) come from the controller. AppKit owns the window frame and
// application menu; the page's title bar sits under the traffic lights and holds the omnibar.
internal sealed partial class WorkspaceWindow : IHostPlatform {
	IWebTransportHub IHostPlatform.Bridge => _bridge;

	// Async, never synchronous: a sync hop from the PTY read thread can deadlock a main-thread PTY write (see HostBridge).
	IUiDispatcher IHostPlatform.Dispatcher => _app.Dispatcher;

	IPtyLauncher IHostPlatform.PtyLauncher => _app.PtyLauncher;

	string IHostPlatform.ChromePlatform => "mac";

	HostTransport IHostPlatform.Transport => HostTransport.Local;

	IReadOnlyList<string> IHostPlatform.Recents => _app.Recents.Items;

	event Action? IHostPlatform.RecentsChanged {
		add => _app.Recents.Changed += value;
		remove => _app.Recents.Changed -= value;
	}

	// AppKit draws the traffic lights over the page's bar; the bar reports caption presses (WorkspaceWindow.Chrome.cs).
	IShellWindow IHostPlatform.Window => this;

	IShellMenuActions IHostPlatform.MenuActions => this;

	IApplicationMenu IHostPlatform.ApplicationMenu => _applicationMenu;

	/// <summary>True when File ▸ Close Window closed this window, so the last one returns to the welcome window.</summary>
	internal bool ClosedToWelcome { get; private set; }

	void IShellMenuActions.CloseWindow() {
		ClosedToWelcome = true;
		Window.PerformClose(null);
	}

	void IShellMenuActions.Quit() {
		var app = NSApplication.SharedApplication;
		app.Terminate(app);
	}

	void IShellMenuActions.ShowOpenFolderPicker() => _app.OpenFolderInteractive();

	void IShellMenuActions.OpenWorkspace(string path) => _app.OpenOrFocus(path);

	IHostDialogs? IHostPlatform.Dialogs => _app.Dialogs;

	Weavie.Core.Sessions.ISystemNotificationChannel IHostPlatform.Notifications => _notifications;

	void IHostPlatform.ToggleWindow() => _app.ToggleWindow(Window);

	void IHostPlatform.ActivateWindow(string? activationToken) => AppDelegate.ActivateWindow(Window);

	// The general pasteboard's plain-text UTI; read + write must agree on it.
	private const string PasteboardTextType = "public.utf8-plain-text";

	// Image UTIs read on a claude-pane paste. A screenshot or Preview copy lands as TIFF; re-encode it to the PNG
	// claude ingests.
	private const string PasteboardPngType = "public.png";
	private const string PasteboardTiffType = "public.tiff";

	// Host-bus handlers enter the main thread before reaching NSPasteboard / NSWorkspace.
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
