using Foundation;
using Weavie.Core.Commands;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Weavie.Hosting;
using Weavie.Mac.Hosting;

namespace Weavie.Mac;

/// <summary>
/// The macOS application delegate: a thin controller over the open <see cref="WorkspaceWindow"/> set. It owns the
/// app-global pieces shared across windows (settings/keybindings via <see cref="HostServices"/>, recents, the native
/// dialogs, the UI-thread marshal, the PTY launcher, and the app-level global hotkeys) and exposes them to each
/// window. Opening a second folder opens a second native window, so normal macOS window switching moves between them.
/// </summary>
[Register("AppDelegate")]
public sealed partial class AppDelegate : NSApplicationDelegate {
	private readonly List<WorkspaceWindow> _windows = [];
	private readonly PosixPtyLauncher _ptyLauncher = new();
	private HostServices? _services;
	private RecentWorkspaces? _recents;
	private MacGlobalHotkeys? _hotkeyRegistrar;
	private MacDialogs? _dialogs;
	private IUiDispatcher? _dispatcher;
	private GlobalHotkeyService? _hotkeys;
	private WorkspaceWindow? _lastActive;

	/// <summary>App-global Core stores (settings, keybindings, theme/remote/rail), shared by every window.</summary>
	internal HostServices Services => _services!;

	/// <summary>Recent workspaces, for File ▸ Open Recent and each window's shell config.</summary>
	internal RecentWorkspaces Recents => _recents!;

	/// <summary>Marshals work onto the main thread; shared by every window's bridge + web surface.</summary>
	internal IUiDispatcher Dispatcher => _dispatcher!;

	/// <summary>The PTY backend launcher (stateless), shared by every window's sessions.</summary>
	internal IPtyLauncher PtyLauncher => _ptyLauncher;

	/// <summary>The native modal file dialogs, shared by every window.</summary>
	internal IHostDialogs Dialogs => _dialogs!;

	/// <summary>
	/// Builds the app-global stores + native pieces, opens the initial workspace window, the menu, and the app-level
	/// global hotkeys, then activates the app.
	/// </summary>
	public override void DidFinishLaunching(NSNotification notification) {
		_services = HostServices.CreateDefault();
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		// A persisted workspace whose folder was since deleted must not strand the app with no window; fall back to home.
		string configured = _services.Settings.GetString("workspace") ?? home;
		string workspace = Directory.Exists(configured) ? configured : home;
		// Surfaces in File ▸ Open Recent + the omnibar shell config.
		_recents = new RecentWorkspaces(new LocalFileSystem(), path: null);

		_dispatcher = new DelegateUiDispatcher(action => {
			if (NSThread.IsMain) {
				action();
			} else {
				NSApplication.SharedApplication.BeginInvokeOnMainThread(action);
			}
		});
		_hotkeyRegistrar = new MacGlobalHotkeys();
		_hotkeyRegistrar.Log += Log;
		_dialogs = new MacDialogs();

		var firstWindow = Open(workspace);

		// The native menu bar; rebuilt on a deferred main-loop turn whenever recents change so File ▸ Open Recent
		// stays current — opening a folder no longer relaunches the app, which used to rebuild it.
		BuildMenu();
		_recents.Changed += () => NSApplication.SharedApplication.BeginInvokeOnMainThread(BuildMenu);

		// Global hotkeys (e.g. ctrl+` → toggle the front window): app-level, so a single registration covers every
		// window instead of each window's core re-registering the same chord. Dispatches to the front window.
		var globalCommands = new CommandDispatcher(_services.CommandRegistry);
		globalCommands.RegisterHandler(CoreCommands.ToggleWindow, (_, _) => {
			_dispatcher.Post(ToggleFrontmost);
			return Task.FromResult(CommandResult.Success("Toggled the Weavie window."));
		});
		_hotkeys = new GlobalHotkeyService(_services.Keybindings, globalCommands, _hotkeyRegistrar);
		_hotkeys.Log += Log;

		NSApplication.SharedApplication.Activate();

		// Unattended screenshot, gated on WEAVIE_SHOT_DIR so the shipped app never writes one.
		if (firstWindow is not null && ScreenshotRequest.FromEnvironment() is { } shot) {
			firstWindow.ScheduleSnapshot(shot);
		}
	}

	/// <summary>Quits the app when its last window is closed.</summary>
	public override bool ApplicationShouldTerminateAfterLastWindowClosed(NSApplication sender) => true;

	/// <summary>Tears down every still-open window's core, then disposes the app-level hotkeys and shared stores on exit.</summary>
	public override void WillTerminate(NSNotification notification) {
		foreach (var window in _windows.ToArray()) {
			window.SaveWindowState();
			window.DisposeCore();
		}

		_windows.Clear();
		_hotkeys?.Dispose(); // also disposes the global hotkey registrar
		_services?.Keybindings.Dispose();
		_services?.Settings.Dispose();
	}

	/// <summary>Records the window that just became key, so the global hotkey + menu commands target the front one.</summary>
	internal void MarkActive(WorkspaceWindow window) => _lastActive = window;

	/// <summary>Saves the closing window's geometry, drops it from the set, and disposes its core.</summary>
	internal void OnWindowClosed(WorkspaceWindow window) {
		window.SaveWindowState();
		_windows.Remove(window);
		if (_lastActive == window) {
			_lastActive = _windows.LastOrDefault();
		}

		window.DisposeCore();
	}

	/// <summary>
	/// Toggles <paramref name="target"/> for <c>weavie.window.toggle</c>: hide the app if active, else activate and
	/// raise the window. Must run on the main thread.
	/// </summary>
	internal void ToggleWindow(NSWindow target) {
		ArgumentNullException.ThrowIfNull(target);
		var app = NSApplication.SharedApplication;
		if (app.Active) {
			app.Hide(app);
		} else {
			app.Activate();
			target.MakeKeyAndOrderFront(null);
		}
	}

	// The front window (last to become key, else the most-recently-opened) — the target for menu commands and the
	// global toggle hotkey.
	private WorkspaceWindow? Frontmost =>
		_lastActive is not null && _windows.Contains(_lastActive) ? _lastActive : _windows.LastOrDefault();

	private void ToggleFrontmost() {
		if (Frontmost is { Window: var target }) {
			ToggleWindow(target);
		}
	}

	// File/View items dispatch the same Weavie command ids the keyboard + omnibar use (routed to the front window),
	// with shortcuts read from the keybinding store. Open Recent reflects the recents at build time.
	private void BuildMenu() =>
		NSApplication.SharedApplication.MainMenu = MacAppMenu.Build(
			runCommand: id => Frontmost?.InvokeCommand(id),
			resolveChord: ResolveChord,
			openFolder: OpenFolderInteractive,
			openRecent: path => OpenOrFocus(path),
			recents: _recents!.Items);

	private static void Log(string line) {
		Console.WriteLine(line);
		Console.Out.Flush();
	}
}
