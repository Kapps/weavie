using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weavie.Core.Layout;

/// <summary>Orientation of a <see cref="SplitNode"/>: children laid out left-to-right or top-to-bottom.</summary>
public enum SplitDirection {
	/// <summary>Children arranged horizontally, left to right.</summary>
	Row,

	/// <summary>Children arranged vertically, top to bottom.</summary>
	Column,
}

/// <summary>
/// A node in the recursive window layout tree: either a <see cref="SplitNode"/> (an oriented, weighted
/// row/column of children) or a leaf <see cref="PaneNode"/> (one surface). Serialized polymorphically
/// with a <c>"type"</c> discriminator so the web layer and Claude see the same shape.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SplitNode), "split")]
[JsonDerivedType(typeof(PaneNode), "pane")]
public abstract record LayoutNode;

/// <summary>An oriented split whose children divide the space by <see cref="Weights"/> (parallel to <see cref="Children"/>).</summary>
public sealed record SplitNode : LayoutNode {
	/// <summary>Whether the children are arranged in a row or a column.</summary>
	public required SplitDirection Dir { get; init; }

	/// <summary>Per-child weights, parallel to <see cref="Children"/>; normalized to sum to 1.</summary>
	public required IReadOnlyList<double> Weights { get; init; }

	/// <summary>The child nodes, in visual order.</summary>
	public required IReadOnlyList<LayoutNode> Children { get; init; }
}

/// <summary>A leaf pane: a stable instance <see cref="Id"/> rendering a registered <see cref="Kind"/>.</summary>
public sealed record PaneNode : LayoutNode {
	/// <summary>Stable instance id (e.g. <c>p_editor</c>); referenced by <see cref="LayoutDocument.Focused"/>.</summary>
	public required string Id { get; init; }

	/// <summary>The registered pane kind (e.g. <c>editor</c>, <c>terminal:claude</c>).</summary>
	public required string Kind { get; init; }
}

/// <summary>Native window geometry: restore bounds plus whether the window is maximized. Host-owned.</summary>
public sealed record WindowState {
	/// <summary>Restore-bounds left edge, in screen pixels.</summary>
	public int X { get; init; }

	/// <summary>Restore-bounds top edge, in screen pixels.</summary>
	public int Y { get; init; }

	/// <summary>Restore-bounds width, in pixels.</summary>
	public int Width { get; init; }

	/// <summary>Restore-bounds height, in pixels.</summary>
	public int Height { get; init; }

	/// <summary>Whether the window should open maximized; the restore bounds remain the un-maximized size.</summary>
	public bool Maximized { get; init; }
}

/// <summary>
/// The persisted window-layout document (<c>~/.weavie/layout.json</c>): the pane <see cref="Root"/>
/// tree, the focused pane, compatibility bookkeeping (<see cref="SeenPaneLevel"/>,
/// <see cref="Dismissed"/>), and host-owned <see cref="Window"/> geometry. Unknown top-level fields
/// (e.g. written by a newer build) round-trip via <see cref="Extra"/>.
/// </summary>
public sealed record LayoutDocument {
	/// <summary>Envelope version — the migration escape hatch reserved for genuine structural reshapes.</summary>
	public int Version { get; init; } = 1;

	/// <summary>Highest pane epoch (<see cref="PaneDefinition.IntroducedIn"/>) this user has been shown.</summary>
	public int SeenPaneLevel { get; init; }

	/// <summary>The pane id holding keyboard focus, or <c>null</c>.</summary>
	public string? Focused { get; init; }

	/// <summary>Pane kinds the user explicitly closed; never auto-reinjected while listed.</summary>
	public IReadOnlyList<string> Dismissed { get; init; } = [];

	/// <summary>Native window geometry, or <c>null</c> to use the default size and position.</summary>
	public WindowState? Window { get; init; }

	/// <summary>The root of the pane layout tree.</summary>
	public required LayoutNode Root { get; init; }

	/// <summary>Unknown top-level fields, preserved verbatim across a load/save round-trip.</summary>
	[JsonExtensionData]
	public IDictionary<string, JsonElement>? Extra { get; init; }
}
