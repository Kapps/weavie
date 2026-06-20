using Weavie.Core.Commands;
using Weavie.Core.Shell;
using Weavie.Hosting;
using Weavie.Win.Hosting;

namespace Weavie.Win;

// The Windows IHostPlatform: the native surface HostCore reaches through. The workspace window owns the bridge,
// the WinForms UI marshal, the ConPTY launcher, the dialogs, and the web title bar (it is the IShellWindow), so
// it implements the seam directly (explicit members keep it off the public API). Global hotkeys are app-level
// (AppController owns one registrar for the whole process), so HotkeyRegistrar is null here. Split from
// WorkspaceWindow.cs so each file holds one concern.
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
}
