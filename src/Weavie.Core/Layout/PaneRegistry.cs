namespace Weavie.Core.Layout;

/// <summary>Coarse placement hint for where a newly-introduced default pane is injected into an existing tree.</summary>
public enum PaneAnchor {
	/// <summary>The main region; only used to seed an empty tree.</summary>
	Main,

	/// <summary>Wrap the tree in a row split, new pane on the far left.</summary>
	FarLeft,

	/// <summary>Wrap the tree in a row split, new pane on the far right.</summary>
	FarRight,

	/// <summary>Wrap the tree in a column split, new pane on top.</summary>
	Top,

	/// <summary>Wrap the tree in a column split, new pane on the bottom.</summary>
	Bottom,

	/// <summary>Best-effort top of the leftmost column (falls back to <see cref="FarLeft"/>).</summary>
	LeftTop,

	/// <summary>Best-effort bottom of the leftmost column (falls back to <see cref="FarLeft"/>).</summary>
	LeftBottom,
}

/// <summary>
/// Declares a pane kind: its identity, whether it's shown by default, the epoch it was introduced in
/// (which drives the appears-once compatibility logic), and where a new default lands. Registered once
/// in <see cref="LayoutPanes"/>; future plugins contribute the same way.
/// </summary>
public sealed record PaneDefinition {
	/// <summary>The pane kind key (e.g. <c>editor</c>, <c>terminal:claude</c>).</summary>
	public required string Kind { get; init; }

	/// <summary>Human- and Claude-facing description.</summary>
	public required string Description { get; init; }

	/// <summary>Whether this pane appears in the default layout and is injected when newly introduced.</summary>
	public bool ShowByDefault { get; init; } = true;

	/// <summary>Monotonic pane epoch; a pane whose epoch exceeds the document's seen level is "new".</summary>
	public int IntroducedIn { get; init; } = 1;

	/// <summary>Where a newly-introduced default pane is inserted into an existing layout.</summary>
	public PaneAnchor DefaultAnchor { get; init; } = PaneAnchor.Main;

	/// <summary>Whether only one instance of this kind may exist (v1: always true).</summary>
	public bool Singleton { get; init; } = true;
}

/// <summary>
/// The catalog of declared pane kinds — the single source of truth for which default panes exist and
/// when they were introduced. This is what makes the layout's forward/backward compatibility
/// data-driven (declare a <see cref="PaneDefinition"/>) rather than code-driven (write a migration).
/// Registration order is preserved.
/// </summary>
public sealed class PaneRegistry {
	private readonly Dictionary<string, PaneDefinition> _byKind = new(StringComparer.Ordinal);
	private readonly List<PaneDefinition> _ordered = [];

	/// <summary>Registers a pane definition. Throws if its <see cref="PaneDefinition.Kind"/> is already taken.</summary>
	public void Register(PaneDefinition pane) {
		ArgumentNullException.ThrowIfNull(pane);
		if (!_byKind.TryAdd(pane.Kind, pane)) {
			throw new InvalidOperationException($"Pane kind '{pane.Kind}' is already registered.");
		}

		_ordered.Add(pane);
	}

	/// <summary>The pane definition for <paramref name="kind"/>, or <c>null</c> if unregistered.</summary>
	public PaneDefinition? Find(string kind) => _byKind.GetValueOrDefault(kind);

	/// <summary>Whether <paramref name="kind"/> is registered.</summary>
	public bool IsKnown(string kind) => _byKind.ContainsKey(kind);

	/// <summary>All registered definitions, in registration order.</summary>
	public IReadOnlyList<PaneDefinition> All => _ordered;

	/// <summary>The highest <see cref="PaneDefinition.IntroducedIn"/> across all registered panes (0 if none).</summary>
	public int CurrentPaneLevel => _ordered.Count == 0 ? 0 : _ordered.Max(static p => p.IntroducedIn);
}
