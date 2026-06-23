namespace Weavie.Core.Theming;

/// <summary>
/// Themes Weavie ships in the web bundle. Their JSON lives on the web side (<c>src/web/src/theme/builtin/*</c>);
/// Core only needs their identity to list/validate/select them (the host sends the id, the web resolves colors).
/// Keep ids in sync with the web registry's <c>BUILTIN_THEMES</c>.
/// </summary>
public static class BuiltInThemes {
	/// <summary>The built-in themes: stable id, display label, base type (<c>dark</c>/<c>light</c>).</summary>
	public static IReadOnlyList<(string Id, string Label, string Type)> All { get; } = [
		("weavie-dark", "Weavie Dark", "dark"),
		("weavie-light", "Weavie Light", "light"),
	];

	/// <summary>True if <paramref name="id"/> is a built-in theme id.</summary>
	public static bool Contains(string id) => All.Any(theme => theme.Id == id);
}
