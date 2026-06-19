using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;

namespace Weavie.Win.Hosting;

/// <summary>
/// The Windows app layer: one per process. Owns the single, app-global <see cref="SettingsStore"/>
/// (shared in-memory across every window, so a live settings/font change reaches them all) and the open
/// workspaces, orchestrated through the Core <see cref="WorkspaceManager"/> (open/focus/dedupe + recents).
/// As the <see cref="ApplicationContext"/>, it keeps the message loop alive across multiple windows.
///
/// Lifecycle: on launch it reopens the last workspace (else the legacy <c>workspace</c> setting, else the
/// welcome window). Opening a folder already open just focuses its window. Closing the last workspace window
/// via File ▸ Close Window falls back to the welcome window; closing it via the title-bar X quits. Closing
/// the welcome window quits. Mac sibling: AppDelegate.
/// </summary>
internal sealed class AppController : ApplicationContext {
	private readonly List<WorkspaceWindow> _windows = [];
	private readonly WorkspaceManager _manager;
	// App-level command dispatcher + global-hotkey plumbing. The global hotkey (ctrl+` → focus) isn't tied to
	// any one window, so its handler lives here and focuses the most-recently-active window.
	private readonly CommandDispatcher _globalCommands;
	private readonly GlobalHotkeyService _hotkeys;
	private WorkspaceWindow? _lastActiveWindow;
	private WelcomeWindow? _welcome;
	private bool _exiting;

	public AppController() {
		// Dark chrome for any WinForms ToolStrip/context menu rendered process-wide (the workspace window's
		// menu bar now lives in the web title bar; this stays for the dark renderer's other consumers).
		AppMenu.UseDarkChrome();

		// User settings (shell / workspace / claude path / fonts) resolved from ~/.weavie/settings.toml;
		// the store is the change hub windows react to (e.g. a shell change reopens the shell pane).
		Settings = CoreSettings.CreateStore(filePath: null, enableWatcher: true);
		Settings.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Commands + keybindings: the app-global command catalog (CoreCommands) and the user keybindings
		// resolved from ~/.weavie/keybindings.json over the command defaults. Both are shared across windows;
		// each window injects them into its web app and re-pushes when the keybindings file changes.
		CommandRegistry = CoreCommands.CreateRegistry();
		Keybindings = new KeybindingStore(CommandRegistry, filePath: null, enableWatcher: true);
		Keybindings.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// App-level dispatcher for commands a global hotkey invokes (the toggle command isn't bound to any one
		// window). The matching per-window handler lives on each session dispatcher, so MCP/palette can toggle
		// the window whose Claude asked. Here, toggle the most-recently-active window.
		_globalCommands = new CommandDispatcher(CommandRegistry);
		_globalCommands.RegisterHandler(CoreCommands.ToggleWindow, (_, _) => {
			ToggleFrontmostWindow();
			return Task.FromResult(CommandResult.Success("Toggled the Weavie window."));
		});

		// Per-theme color overrides (~/.weavie/theme-overrides.json) — app-global like settings so a change
		// reaches every window; appearance itself is normal settings (theme.mode/theme.light/theme.dark).
		ThemeOverrides = new ThemeOverridesStore(new LocalFileSystem(), path: null);
		ThemeOverrides.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Recent workspaces (~/.weavie/recents.json) drive reopen-last-on-launch and the Open Recent menu;
		// the manager wraps them with open/focus/dedupe so the logic isn't duplicated on macOS.
		var recents = new RecentWorkspaces(new LocalFileSystem(), path: null);
		recents.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};
		_manager = new WorkspaceManager(recents);

		string? initial = ResolveInitialWorkspace();
		if (initial is null || OpenOrFocus(initial) is null) {
			ShowWelcome();
		}

		// Global hotkeys (e.g. ctrl+` → focus Weavie). Created last, after a window exists: constructing a
		// control installs the WinForms SynchronizationContext that WindowsGlobalHotkeys captures + marshals
		// its RegisterHotKey calls onto. The service reads the global bindings from Keybindings and re-applies
		// on edit; the registrar posts the actual registration to run once the message loop starts pumping.
		var registrar = new WindowsGlobalHotkeys();
		registrar.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};
		_hotkeys = new GlobalHotkeyService(Keybindings, _globalCommands, registrar);
		_hotkeys.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};
	}

	/// <summary>The single app-global settings store, shared by every workspace window.</summary>
	public SettingsStore Settings { get; }

	/// <summary>The app-global command catalog (the built-in <see cref="CoreCommands"/>), shared by every window.</summary>
	public CommandRegistry CommandRegistry { get; }

	/// <summary>The app-global keybindings store (user file merged over command defaults), shared by every window.</summary>
	public KeybindingStore Keybindings { get; }

	/// <summary>The recent-workspaces store, for the Open Recent menu and the welcome window.</summary>
	public RecentWorkspaces Recents => _manager.Recents;

	/// <summary>The app-global per-theme color overrides store (theme-overrides.json), shared by every window.</summary>
	public ThemeOverridesStore ThemeOverrides { get; }

	/// <summary>
	/// Opens <paramref name="root"/> as a workspace: focuses the existing window if that folder is already
	/// open, otherwise opens a new window and dismisses the welcome window. Records the folder in recents.
	/// Returns the window, or <c>null</c> if the folder no longer exists (its recents entry is pruned).
	/// </summary>
	public WorkspaceWindow? OpenOrFocus(string root) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		if (!Directory.Exists(root)) {
			_manager.Recents.Remove(root);
			return null;
		}

		var opened = _manager.Open(root);
		var existing = _windows.FirstOrDefault(w => w.Id == opened.Id);
		if (existing is not null) {
			_lastActiveWindow = existing;
			Activate(existing);
			return existing;
		}

		var window = new WorkspaceWindow(this, opened.Root);
		_windows.Add(window);
		window.Activated += (_, _) => _lastActiveWindow = window;
		window.FormClosed += (_, _) => OnWorkspaceWindowClosed(window);
		_lastActiveWindow = window;
		window.Show();
		CloseWelcome();
		return window;
	}

	/// <summary>Shows the native folder picker (starting near the last workspace) and opens the chosen folder.</summary>
	public void OpenFolderInteractive(IWin32Window owner) {
		ArgumentNullException.ThrowIfNull(owner);
		using var dialog = new FolderBrowserDialog {
			Description = "Open Folder as Workspace",
			UseDescriptionForTitle = true,
			ShowNewFolderButton = false,
		};
		string? last = _manager.Recents.LastOpened;
		if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) {
			dialog.InitialDirectory = last;
		}

		if (dialog.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrEmpty(dialog.SelectedPath)) {
			OpenOrFocus(dialog.SelectedPath);
		}
	}

	/// <summary>Quits the app: closes the welcome window and every workspace window, then ends the message loop.</summary>
	public void Quit() {
		_exiting = true;
		var toClose = _windows.ToArray();
		CloseWelcome();
		if (toClose.Length == 0) {
			ExitThread();
			return;
		}

		foreach (var window in toClose) {
			window.Close(); // the last close handler calls ExitThread
		}
	}

	private static void Activate(Form window) {
		if (window.WindowState == FormWindowState.Minimized) {
			window.WindowState = FormWindowState.Normal;
		}

		window.Activate();
		window.BringToFront();
	}

	/// <summary>
	/// Toggles the most-recently-active workspace window (else the welcome window) — the handler behind the
	/// global hotkey and <c>weavie.window.toggle</c>: focus it when it's behind, or drop it behind (handing
	/// focus back to the previously focused window) when it's already in front. A no-op when nothing is open.
	/// Marshals onto the target window's UI thread.
	/// </summary>
	private void ToggleFrontmostWindow() {
		Form? target = _lastActiveWindow is not null && _windows.Contains(_lastActiveWindow)
			? _lastActiveWindow
			: _windows.Count > 0 ? _windows[^1] : _welcome;
		if (target is null) {
			return;
		}

		if (target.InvokeRequired) {
			target.BeginInvoke(() => WindowFocus.Toggle(target));
		} else {
			WindowFocus.Toggle(target);
		}
	}

	private void OnWorkspaceWindowClosed(WorkspaceWindow window) {
		_windows.Remove(window);
		if (_lastActiveWindow == window) {
			_lastActiveWindow = _windows.Count > 0 ? _windows[^1] : null;
		}

		_manager.Close(window.Id);
		if (_exiting) {
			if (_windows.Count == 0) {
				ExitThread();
			}

			return;
		}

		if (_windows.Count > 0) {
			return;
		}

		// Last workspace window closed. Fall back to the welcome window only when the user chose
		// File ▸ Close Window (an intentional "close this workspace, keep the app" gesture); closing via
		// the title-bar X / Alt+F4 quits instead — there's nothing left to show.
		if (window.ClosedToWelcome) {
			ShowWelcome();
		} else {
			_exiting = true;
			ExitThread();
		}
	}

	private void ShowWelcome() {
		if (_welcome is not null) {
			Activate(_welcome);
			return;
		}

		_welcome = new WelcomeWindow(this);
		_welcome.FormClosed += (_, _) => OnWelcomeClosed();
		_welcome.Show();
	}

	private void CloseWelcome() => _welcome?.Close();

	private void OnWelcomeClosed() {
		_welcome = null;
		// Closing the welcome window with nothing else open quits the app.
		if (!_exiting && _windows.Count == 0) {
			_exiting = true;
			ExitThread();
		}
	}

	/// <summary>
	/// Picks the workspace to reopen on launch: the most-recently-opened folder if it still exists, else the
	/// legacy <c>workspace</c> setting (kept as a migration source), else <c>null</c> (show the welcome window).
	/// </summary>
	private string? ResolveInitialWorkspace() {
		string? last = _manager.Recents.LastOpened;
		if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) {
			return last;
		}

		// The `workspace` setting is a migration source only when EXPLICITLY set — its computed default is
		// the home directory, which we don't want to silently auto-open as a "project" (that's exactly what
		// the welcome window is for). So honor it only when its source isn't the registered default.
		var configured = Settings.Resolve("workspace");
		if (configured.Source != SettingSource.Default
			&& configured.Value is string root
			&& !string.IsNullOrEmpty(root)
			&& Directory.Exists(root)) {
			return root;
		}

		return null;
	}

	/// <inheritdoc/>
	protected override void Dispose(bool disposing) {
		if (disposing) {
			_hotkeys.Dispose(); // unregisters the OS hotkeys + tears down the message window
			Settings.Dispose();
			Keybindings.Dispose();
		}

		base.Dispose(disposing);
	}
}
