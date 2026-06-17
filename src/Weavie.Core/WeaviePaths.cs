using Weavie.Core.Workspaces;

namespace Weavie.Core;

/// <summary>
/// Single source of truth for Weavie's on-disk locations. Every subsystem — settings, themes, and
/// host-internal caches — resolves its path from here so nothing hardcodes its own. All Weavie data
/// lives under the cross-platform Weavie root, <c>~/.weavie</c>.
/// </summary>
public static class WeaviePaths {
	/// <summary>The Weavie root — the user's home directory plus <c>.weavie</c> (e.g. <c>~/.weavie</c>).</summary>
	public static string Root { get; } =
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weavie");

	/// <summary>Where user settings live: <c>~/.weavie/settings</c>.</summary>
	public static string Settings { get; } = Path.Combine(Root, "settings");

	/// <summary>The user settings file: <c>~/.weavie/settings.toml</c>.</summary>
	public static string SettingsFile { get; } = Path.Combine(Root, "settings.toml");

	/// <summary>The legacy single-window layout (pane tree + window geometry): <c>~/.weavie/layout.json</c>. Superseded by per-workspace layout files; kept for the default store and back-compat.</summary>
	public static string LayoutFile { get; } = Path.Combine(Root, "layout.json");

	/// <summary>Root for per-workspace state (each workspace's layout + window geometry): <c>~/.weavie/workspaces</c>.</summary>
	public static string Workspaces { get; } = Path.Combine(Root, "workspaces");

	/// <summary>The persisted most-recently-opened workspace list: <c>~/.weavie/recents.json</c>.</summary>
	public static string RecentsFile { get; } = Path.Combine(Root, "recents.json");

	/// <summary>Where installed and built-in themes live: <c>~/.weavie/themes</c>.</summary>
	public static string Themes { get; } = Path.Combine(Root, "themes");

	/// <summary>Root for host-internal caches (e.g. the WebView2 user-data folder): <c>~/.weavie/internals</c>.</summary>
	public static string Internals { get; } = Path.Combine(Root, "internals");

	/// <summary>
	/// Resolves a named host-internal cache folder under <see cref="Internals"/>,
	/// e.g. <c>Internal("webview2")</c> → <c>~/.weavie/internals/webview2</c>.
	/// </summary>
	/// <param name="name">The cache folder name.</param>
	/// <returns>The absolute path to that folder under <see cref="Internals"/>.</returns>
	public static string Internal(string name) => Path.Combine(Internals, name);

	/// <summary>
	/// This workspace's state folder, keyed by its <see cref="WorkspaceId"/>:
	/// <c>~/.weavie/workspaces/&lt;id&gt;</c>.
	/// </summary>
	/// <param name="id">The workspace identity (a path-derived digest).</param>
	/// <returns>The absolute path to that workspace's state folder.</returns>
	public static string WorkspaceDir(WorkspaceId id) => Path.Combine(Workspaces, id.Value);

	/// <summary>
	/// This workspace's pane layout + window geometry:
	/// <c>~/.weavie/workspaces/&lt;id&gt;/layout.json</c>.
	/// </summary>
	/// <param name="id">The workspace identity (a path-derived digest).</param>
	/// <returns>The absolute path to that workspace's layout file.</returns>
	public static string WorkspaceLayoutFile(WorkspaceId id) => Path.Combine(WorkspaceDir(id), "layout.json");
}
