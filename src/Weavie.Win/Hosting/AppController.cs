using Weavie.Core.Configuration;

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

	public AppController() {
		// User settings (shell / workspace / claude path / fonts) resolved from ~/.weavie/settings.toml;
		// the store is the change hub windows react to (e.g. a shell change reopens the shell pane).
		Settings = CoreSettings.CreateStore();
		Settings.Log += line => {
			Console.WriteLine(line);
			Console.Out.Flush();
		};

		string root = Settings.GetString("workspace")
			?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var window = OpenWorkspace(root);
		// Closing the sole window exits the app — matches the previous Application.Run(new MainForm()).
		MainForm = window;
		window.Show();
	}

	/// <summary>The single app-global settings store, shared by every workspace window.</summary>
	public SettingsStore Settings { get; }

	/// <summary>Opens a workspace window rooted at <paramref name="root"/> and tracks it. Does not show it.</summary>
	public WorkspaceWindow OpenWorkspace(string root) {
		var window = new WorkspaceWindow(this, root);
		_windows.Add(window);
		window.FormClosed += (_, _) => _windows.Remove(window);
		return window;
	}

	/// <inheritdoc/>
	protected override void Dispose(bool disposing) {
		if (disposing) {
			Settings.Dispose();
		}

		base.Dispose(disposing);
	}
}
