using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Weavie.Hosting;

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
	private readonly ApplicationHotkeys _hotkeys;
	private WorkspaceWindow? _lastActiveWindow;
	private WelcomeWindow? _welcome;
	private bool _exiting;

	public AppController() {
		// App-global Core stores shared by every window (and the welcome window); installs the console tee first.
		Services = HostServices.CreateDefault();
		Notifications = new WindowsNotificationService();

		// Recent workspaces (~/.weavie/recents.json) drive reopen-last-on-launch and the Open Recent menu;
		// the manager wraps them with open/focus/dedupe.
		var recents = new RecentWorkspaces(new LocalFileSystem(), path: null);
		recents.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};
		_manager = new WorkspaceManager(recents);

		string? initial = InitialWorkspace.Resolve(Services.Settings, _manager.Recents);
		if (initial is null || OpenOrFocus(initial) is null) {
			ShowWelcome();
		}

		// Global hotkeys (e.g. ctrl+` → focus). Created last, after a window exists, so the WinForms
		// SynchronizationContext WindowsGlobalHotkeys captures is installed.
		_hotkeys = new ApplicationHotkeys(
			Services.CommandRegistry,
			Services.Keybindings,
			new WindowsGlobalHotkeys(),
			ToggleFrontmostWindow,
			line => {
				Console.WriteLine(line);
				Console.Out.Flush();
			});
	}

	/// <summary>The app-global Core stores, shared by every workspace window and the welcome window.</summary>
	public HostServices Services { get; }

	/// <summary>Recent-workspaces store, for the Open Recent menu and the welcome window.</summary>
	public RecentWorkspaces Recents => _manager.Recents;

	/// <summary>The process-wide Windows app-notification manager shared by every workspace channel.</summary>
	public WindowsNotificationService Notifications { get; }

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

	/// <inheritdoc/>
	protected override void Dispose(bool disposing) {
		if (disposing) {
			_hotkeys.Dispose(); // unregisters the OS hotkeys + tears down the message window
			Notifications.Dispose();
			Services.Settings.Dispose();
			Services.Keybindings.Dispose();
		}

		base.Dispose(disposing);
	}
}
