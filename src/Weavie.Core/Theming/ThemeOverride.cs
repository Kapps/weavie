using System.Text.Json.Serialization;

namespace Weavie.Core.Theming;

/// <summary>
/// One declarative theme override op, layered in order over the active theme's color palette. Two kinds
/// compose freely: a per-key <see cref="ThemeOverrideSet"/> and a whole-palette
/// <see cref="ThemeOverrideTransform"/>. Serialized with a <c>kind</c> discriminator matching the web
/// resolver's <c>OverrideOp</c> shape (<c>src/web/src/theme/overrides.ts</c>), so the same JSON both
/// persists to <c>~/.weavie/theme-overrides.json</c> and ships to the web unchanged.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ThemeOverrideSet), "set")]
[JsonDerivedType(typeof(ThemeOverrideTransform), "transform")]
public abstract record ThemeOverrideOp;

/// <summary>
/// Directly styles one entry. Targets the workbench <c>colors</c> table by default (e.g.
/// <c>editor.background</c> → <c>#000000</c>), or a syntax table when <see cref="Table"/> is set. Sets a
/// color (<see cref="Value"/>), a font style (<see cref="FontStyle"/>, syntax tables only), or both; at
/// least one is present.
/// </summary>
public sealed record ThemeOverrideSet : ThemeOverrideOp {
	/// <summary>Target table: <c>colors</c> (default, omitted), <c>tokenColors</c>, or <c>semanticTokenColors</c>.</summary>
	[JsonPropertyName("table")]
	public string? Table { get; init; }

	/// <summary>The color id / scope / selector to set (interpreted per <see cref="Table"/>).</summary>
	[JsonPropertyName("key")]
	public required string Key { get; init; }

	/// <summary>Hex color to set. Null when the op only changes <see cref="FontStyle"/>.</summary>
	[JsonPropertyName("value")]
	public string? Value { get; init; }

	/// <summary>
	/// Font style — a space-separated subset of <c>italic bold underline strikethrough</c>, or <c>""</c> to
	/// clear inherited styles. Only meaningful on syntax tables; null leaves the style untouched.
	/// </summary>
	[JsonPropertyName("fontStyle")]
	public string? FontStyle { get; init; }
}

/// <summary>A parametric op over the whole palette, so the user need not hand-edit hundreds of keys.</summary>
public sealed record ThemeOverrideTransform : ThemeOverrideOp {
	/// <summary>One of <c>darken</c>, <c>lighten</c>, <c>saturate</c>, <c>desaturate</c>, <c>contrast</c>.</summary>
	[JsonPropertyName("op")]
	public required string Op { get; init; }

	/// <summary>0..1 fraction (e.g. <c>0.2</c> = "20% darker").</summary>
	[JsonPropertyName("amount")]
	public required double Amount { get; init; }

	/// <summary>
	/// Which table(s) to affect: <c>all</c> (default when null), <c>colors</c>, <c>tokenColors</c>,
	/// <c>semanticTokenColors</c>, or <c>syntax</c> (both syntax tables).
	/// </summary>
	[JsonPropertyName("target")]
	public string? Target { get; init; }
}
