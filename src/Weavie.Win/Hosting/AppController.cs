using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Remote;
using Weavie.Core.Sessions;
using Weavie.Core.Theming;
using Weavie.Core.Workspaces;

namespace Weavie.Win.Hosting;

/// <summary>
/// The Windows app layer (one per process): owns the app-global stores shared across every window and the open
/// workspaces (via the Core <see cref="WorkspaceManager"/>), and as the <see cref="ApplicationContext"/> keeps the
/// message loop alive across windows.
/// <para>
/// Lifecycle: launch reopens the last workspace (else the <c>workspace</c> setting, else the welcome window);
/// opening an already-open folder focuses its window; closing the last workspace window via File ▸ Close Window
/// falls back to the welcome window, while the title-bar X quits; closing the welcome window quits.
/// </para>
/// </summary>
internal sealed class AppController : ApplicationContext {
	private readonly List<WorkspaceWindow> _windows = [];
	private readonly WorkspaceManager _manager;
	// Global-hotkey commands (ctrl+` → focus) aren't tied to one window, so their handlers live here.
	private readonly CommandDispatcher _globalCommands;
	private readonly GlobalHotkeyService _hotkeys;
	private WorkspaceWindow? _lastActiveWindow;
	private WelcomeWindow? _welcome;
	private bool _exiting;

	public AppController() {
		// Dark chrome for any WinForms ToolStrip/context menu rendered process-wide.
		AppMenu.UseDarkChrome();

		// User settings from ~/.weavie/settings.toml; the change hub windows react to (e.g. a shell change reopens
		// the shell pane).
		Settings = CoreSettings.CreateStore(filePath: null, enableWatcher: true);
		Settings.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// App-global command catalog + user keybindings (~/.weavie/keybindings.json merged over defaults); each
		// window injects them into its web app and re-pushes on edit.
		CommandRegistry = CoreCommands.CreateRegistry();
		Keybindings = new KeybindingStore(CommandRegistry, filePath: null, enableWatcher: true);
		Keybindings.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Dispatcher for global-hotkey commands; toggle targets the most-recently-active window.
		_globalCommands = new CommandDispatcher(CommandRegistry);
		_globalCommands.RegisterHandler(CoreCommands.ToggleWindow, (_, _) => {
			ToggleFrontmostWindow();
			return Task.FromResult(CommandResult.Success("Toggled the Weavie window."));
		});

		// Claude session ids per working directory (~/.weavie/claude-sessions.json) — app-global so every session
		// resumes its own directory's previous Claude conversation.
		ClaudeSessions = new ClaudeSessionStore(new LocalFileSystem(), WeaviePaths.ClaudeSessionsFile);
		ClaudeSessions.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Per-theme color overrides (~/.weavie/theme-overrides.json) — app-global so a change reaches every window;
		// appearance itself is normal settings (theme.mode/theme.light/theme.dark).
		ThemeOverrides = new ThemeOverridesStore(new LocalFileSystem(), path: null);
		ThemeOverrides.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Registered remote agents (~/.weavie/remote-agents.json) — app-global so a connect/disconnect in one
		// window reaches every other window's rail.
		RemoteAgents = new RemoteAgentStore(new LocalFileSystem(), path: null);
		RemoteAgents.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Session rail UI state (~/.weavie/rail-state.json) — last-used backend + promoted remote sessions;
		// app-global so it's shared across windows.
		RailState = new RailStateStore(new LocalFileSystem(), path: null);
		RailState.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Recent workspaces (~/.weavie/recents.json) drive reopen-last-on-launch and the Open Recent menu;
		// the manager wraps them with open/focus/dedupe.
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

		// Global hotkeys (e.g. ctrl+` → focus). Created last, after a window exists, so the WinForms
		// SynchronizationContext WindowsGlobalHotkeys captures is installed.
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

	/// <summary>App-global settings store, shared by every workspace window.</summary>
	public SettingsStore Settings { get; }

	/// <summary>App-global command catalog (<see cref="CoreCommands"/>), shared by every window.</summary>
	public CommandRegistry CommandRegistry { get; }

	/// <summary>App-global keybindings store (user file merged over command defaults), shared by every window.</summary>
	public KeybindingStore Keybindings { get; }

	/// <summary>Recent-workspaces store, for the Open Recent menu and the welcome window.</summary>
	public RecentWorkspaces Recents => _manager.Recents;

	/// <summary>App-global per-theme color overrides store (theme-overrides.json), shared by every window.</summary>
	public ThemeOverridesStore ThemeOverrides { get; }

	/// <summary>App-global Claude-session-id map (claude-sessions.json), shared so every session resumes its own.</summary>
	public ClaudeSessionStore ClaudeSessions { get; }

	/// <summary>App-global remote-agent registry (remote-agents.json), shared so a connect/disconnect reaches every window.</summary>
	public RemoteAgentStore RemoteAgents { get; }

	/// <summary>App-global session-rail UI state (rail-state.json), shared across windows.</summary>
	public RailStateStore RailState { get; }

	/// <summary>
	/// Opens <paramref name="root"/> as a workspace: focuses the existing window if already open, else opens a new
	/// one (dismissing the welcome window) and records it in recents. Returns the window, or <c>null</c> if the
	/// folder no longer exists (its recents entry is pruned).
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
	/// Toggles the most-recently-active workspace window (else the welcome window) for the global hotkey and
	/// <c>weavie.window.toggle</c>: focus it when behind, drop it behind when in front. No-op when nothing is open;
	/// marshals onto the target window's UI thread.
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

		// Last window closed: fall back to the welcome window only for File ▸ Close Window; the title-bar X / Alt+F4
		// quits instead.
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
	/// Picks the workspace to reopen on launch: the last-opened folder if it still exists, else the explicitly-set
	/// <c>workspace</c> setting, else <c>null</c> (show the welcome window).
	/// </summary>
	private string? ResolveInitialWorkspace() {
		string? last = _manager.Recents.LastOpened;
		if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) {
			return last;
		}

		// Honor `workspace` only when EXPLICITLY set: its computed default is the home directory, which shouldn't
		// auto-open as a project.
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
