using System.Text.Json.Serialization;

namespace Weavie.Core.Theming;

/// <summary>
/// One declarative theme override op (spec §6), layered in order over the active theme's color palette.
/// Two kinds compose freely: a per-key <see cref="ThemeOverrideSet"/> and a whole-palette
/// <see cref="ThemeOverrideTransform"/>. Serialized with a <c>kind</c> discriminator to exactly match the
/// web resolver's <c>OverrideOp</c> shape (<c>src/web/src/theme/overrides.ts</c>), so the same JSON both
/// persists to <c>~/.weavie/theme-overrides.json</c> and ships to the web over the bridge unchanged.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ThemeOverrideSet), "set")]
[JsonDerivedType(typeof(ThemeOverrideTransform), "transform")]
public abstract record ThemeOverrideOp;

/// <summary>
/// Directly sets one color. By default the workbench <c>colors</c> table (e.g. <c>editor.background</c> →
/// <c>#000000</c>); with <see cref="Table"/> set, a syntax table — a TextMate scope in <c>tokenColors</c> or
/// a semantic selector in <c>semanticTokenColors</c>. Last write wins.
/// </summary>
public sealed record ThemeOverrideSet : ThemeOverrideOp {
	/// <summary>Target table: <c>colors</c> (default, omitted), <c>tokenColors</c>, or <c>semanticTokenColors</c>.</summary>
	[JsonPropertyName("table")]
	public string? Table { get; init; }

	/// <summary>The color id / scope / selector to set (interpreted per <see cref="Table"/>).</summary>
	[JsonPropertyName("key")]
	public required string Key { get; init; }

	/// <summary>The hex color to set it to.</summary>
	[JsonPropertyName("value")]
	public required string Value { get; init; }
}

/// <summary>A parametric op over the whole palette, so the user need not hand-edit hundreds of keys.</summary>
public sealed record ThemeOverrideTransform : ThemeOverrideOp {
	/// <summary>One of <c>darken</c>, <c>lighten</c>, <c>saturate</c>, <c>desaturate</c>, <c>contrast</c>.</summary>
	[JsonPropertyName("op")]
	public required string Op { get; init; }

	/// <summary>0..1 fraction (e.g. <c>0.2</c> = "20% darker").</summary>
	[JsonPropertyName("amount")]
	public required double Amount { get; init; }

	/// <summary>Which keys to affect; only <c>all</c> for now (defaults to all when null).</summary>
	[JsonPropertyName("target")]
	public string? Target { get; init; }
}
