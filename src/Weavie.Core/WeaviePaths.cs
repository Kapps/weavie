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

	/// <summary>The persisted window layout (pane tree + window geometry): <c>~/.weavie/layout.json</c>.</summary>
	public static string LayoutFile { get; } = Path.Combine(Root, "layout.json");

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
}
