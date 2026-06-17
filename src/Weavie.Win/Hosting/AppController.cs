using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;

namespace Weavie.Win.Hosting;

/// <summary>
/// The Windows app layer: one per process. Owns the single, app-global <see cref="SettingsStore"/>
/// (shared in-memory across every window, so a live settings/font change reaches them all) and the set
/// of open <see cref="WorkspaceWindow"/>s. As the <see cref="ApplicationContext"/>, it bootstraps the
/// first workspace and keeps the message loop alive.
///
/// v1 opens exactly one workspace (the resolved <c>workspace</c> setting) and exits when its window
/// closes — parity with the previous single-window app. Multi-window / open-folder / restore land in
/// later phases; <see cref="OpenWorkspace"/> is the seam they build on. Mac sibling: AppDelegate.
/// </summary>
internal sealed class AppController : ApplicationContext {
	private readonly List<WorkspaceWindow> _windows = [];
	private readonly RecentWorkspaces _recents;

	public AppController() {
		// User settings (shell / workspace / claude path / fonts) resolved from ~/.weavie/settings.toml;
		// the store is the change hub windows react to (e.g. a shell change reopens the shell pane).
		Settings = CoreSettings.CreateStore();
		Settings.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		// Recent workspaces (~/.weavie/recents.json): drives reopen-last-on-launch and the Open Recent menu.
		_recents = new RecentWorkspaces(new LocalFileSystem());
		_recents.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		var window = OpenWorkspace(ResolveInitialWorkspace());
		// Closing the sole window exits the app — matches the previous Application.Run(new MainForm()).
		MainForm = window;
		window.Show();
	}

	/// <summary>The single app-global settings store, shared by every workspace window.</summary>
	public SettingsStore Settings { get; }

	/// <summary>
	/// Opens a workspace window rooted at <paramref name="root"/>, records it as most-recent, and tracks
	/// it. Does not show it. The root is normalized so recents and per-workspace state agree.
	/// </summary>
	public WorkspaceWindow OpenWorkspace(string root) {
		ArgumentException.ThrowIfNullOrEmpty(root);
		string full = Path.GetFullPath(root);
		_recents.Add(full);
		var window = new WorkspaceWindow(this, full);
		_windows.Add(window);
		window.FormClosed += (_, _) => _windows.Remove(window);
		return window;
	}

	/// <summary>
	/// Picks the workspace to open on launch: the most-recently-opened folder if it still exists, else the
	/// legacy <c>workspace</c> setting (kept as a migration source), else the user's home directory.
	/// </summary>
	private string ResolveInitialWorkspace() {
		string? last = _recents.LastOpened;
		if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) {
			return last;
		}

		string? configured = Settings.GetString("workspace");
		if (!string.IsNullOrEmpty(configured) && Directory.Exists(configured)) {
			return configured;
		}

		return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	}

	/// <inheritdoc/>
	protected override void Dispose(bool disposing) {
		if (disposing) {
			Settings.Dispose();
		}

		base.Dispose(disposing);
	}
}
