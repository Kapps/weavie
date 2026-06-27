using Weavie.Core.Configuration;
using Weavie.Core.Workspaces;

namespace Weavie.Hosting;

/// <summary>
/// The single source of truth every GUI host shares for "what to open on launch": the most-recently-opened
/// folder if it still exists, else the explicitly-set <c>workspace</c> setting if it does, else <c>null</c> —
/// the empty state, which shows the welcome screen. Never the home directory: an unset workspace is no workspace.
/// </summary>
public static class InitialWorkspace {
	/// <summary>Resolves the launch workspace, or <c>null</c> when there is none (show the welcome screen).</summary>
	public static string? Resolve(SettingsStore settings, RecentWorkspaces recents) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(recents);

		string? last = recents.LastOpened;
		if (!string.IsNullOrEmpty(last) && Directory.Exists(last)) {
			return last;
		}

		string? configured = settings.GetString("workspace");
		return !string.IsNullOrEmpty(configured) && Directory.Exists(configured) ? configured : null;
	}
}
