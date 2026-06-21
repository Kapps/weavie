using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;

namespace Weavie.Hosting;

/// <summary>
/// The single-window host bootstrap: the one workspace a host serves (the <c>workspace</c> setting, else the
/// user's home directory) plus a recents list seeded with it. Shared by the GTK and macOS hosts, which each
/// serve a single workspace per process. The Windows multi-window host resolves its own way (last-opened /
/// explicit setting, with a welcome window for the no-project case), so it does not use this.
/// </summary>
public static class WorkspaceBootstrap {
	/// <summary>Resolves the served workspace and a recents list already seeded with it.</summary>
	public static (string Workspace, RecentWorkspaces Recents) Resolve(SettingsStore settings) {
		ArgumentNullException.ThrowIfNull(settings);
		string workspace = settings.GetString("workspace")
			?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var recents = new RecentWorkspaces(new LocalFileSystem(), path: null);
		recents.Add(workspace);
		return (workspace, recents);
	}
}
