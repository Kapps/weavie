namespace Weavie.Core.Theming;

/// <summary>
/// One color theme an installed VS Code extension contributes (from its <c>package.json</c>
/// <c>contributes.themes[]</c>). The install unit is the extension, the selection unit the individual theme
/// (one extension can ship several). The raw theme JSON at <see cref="Path"/> is the lossless source of
/// truth, converted to Monaco/xterm/CSS at load.
/// </summary>
/// <param name="Label">Display name (e.g. <c>"Dracula"</c>).</param>
/// <param name="UiTheme">Base kind: <c>vs</c> (light), <c>vs-dark</c> (dark), or <c>hc-*</c> (high contrast).</param>
/// <param name="Path">Absolute path to the theme's JSON file.</param>
public sealed record ThemeContribution(string Label, string UiTheme, string Path);

/// <summary>
/// An installed theme recorded in <c>~/.weavie/themes/index.json</c>: the selectable unit, pointing back to
/// its source extension/version so installs can be enumerated and updated.
/// </summary>
/// <param name="Id">Stable id, e.g. <c>"dracula-theme.theme-dracula/Dracula"</c>.</param>
/// <param name="Label">Display name shown in the theme picker.</param>
/// <param name="UiTheme">Base kind (<c>vs</c> / <c>vs-dark</c> / <c>hc-*</c>).</param>
/// <param name="Namespace">Open VSX namespace (publisher).</param>
/// <param name="Name">Open VSX extension name.</param>
/// <param name="Version">Installed extension version.</param>
/// <param name="Path">Absolute path to the theme's JSON file.</param>
public sealed record InstalledTheme(
	string Id,
	string Label,
	string UiTheme,
	string Namespace,
	string Name,
	string Version,
	string Path);
