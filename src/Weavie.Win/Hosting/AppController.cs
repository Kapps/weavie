using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;

namespace Weavie.Win.Hosting;

/// <summary>
/// The Windows app layer: one per process. Owns the single, app-global <see cref="SettingsStore"/>
/// (shared in-memory across every window, so a live settings/font change reaches them all) and the open
/// workspaces, orchestrated through the Core <see cref="WorkspaceManager"/> (open/focus/dedupe + recents).
/// As the <see cref="ApplicationContext"/>, it keeps the message loop alive across multiple windows.
///
/// Lifecycle: on launch it reopens the last workspace (else the legacy <c>workspace</c> setting, else the
/// welcome window). Opening a folder already open just focuses its window. When the last workspace window
/// closes, the welcome window appears; closing the welcome window quits. Mac sibling: AppDelegate.
/// </summary>
internal sealed class AppController : ApplicationContext {
	private readonly List<WorkspaceWindow> _windows = [];
	private readonly WorkspaceManager _manager;
	private WelcomeWindow? _welcome;
	private bool _exiting;

	public AppController() {
		// Dark menu chrome process-wide before any window builds its menu.
		AppMenu.UseDarkChrome();

		// User settings (shell / workspace / claude path / fonts) resolved from ~/.weavie/settings.toml;
		// the store is the change hub windows react to (e.g. a shell change reopens the shell pane).
		Settings = CoreSettings.CreateStore();
		Settings.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Recent workspaces (~/.weavie/recents.json) drive reopen-last-on-launch and the Open Recent menu;
		// the manager wraps them with open/focus/dedupe so the logic isn't duplicated on macOS.
		var recents = new RecentWorkspaces(new LocalFileSystem());
		recents.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};
		_manager = new WorkspaceManager(recents);

		string? initial = ResolveInitialWorkspace();
		if (initial is null || OpenOrFocus(initial) is null) {
			ShowWelcome();
		}
	}

	/// <summary>The single app-global settings store, shared by every workspace window.</summary>
	public SettingsStore Settings { get; }

	/// <summary>The recent-workspaces store, for the Open Recent menu and the welcome window.</summary>
	public RecentWorkspaces Recents => _manager.Recents;

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
			Activate(existing);
			return existing;
		}

		var window = new WorkspaceWindow(this, opened.Root);
		_windows.Add(window);
		window.FormClosed += (_, _) => OnWorkspaceWindowClosed(window);
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

	private void OnWorkspaceWindowClosed(WorkspaceWindow window) {
		_windows.Remove(window);
		_manager.Close(window.Id);
		if (_exiting) {
			if (_windows.Count == 0) {
				ExitThread();
			}

			return;
		}

		// Last workspace window closed → fall back to the welcome window rather than quitting.
		if (_windows.Count == 0) {
			ShowWelcome();
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
			Settings.Dispose();
		}

		base.Dispose(disposing);
	}
}
