namespace Weavie.Core.Layout;

/// <summary>The result of <see cref="LayoutReconciler.Reconcile"/>: the cleaned document, whether it changed, and notes.</summary>
public sealed record ReconcileOutcome(LayoutDocument Document, bool Mutated, IReadOnlyList<string> Notes);

/// <summary>
/// Reconciles a persisted (possibly old, newer, or hand-broken) layout document against the current
/// <see cref="PaneRegistry"/> — pure and deterministic. Prunes unknown kinds, injects newly-introduced defaults,
/// bumps the seen-level watermark, normalizes and repairs: the whole compat story. See <c>docs/specs/layout.md</c>.
/// </summary>
public static class LayoutReconciler {
	private const double MinWeight = 0.05;
	private const double InjectWeight = 0.2;

	/// <summary>Reconciles <paramref name="document"/> against <paramref name="registry"/>, returning the cleaned document and notes.</summary>
	public static ReconcileOutcome Reconcile(LayoutDocument document, PaneRegistry registry) {
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(registry);

		var notes = new List<string>();

		// 1. Migrate unstable pane kinds, then prune panes whose kind is no longer registered.
		var root = Prune(MigrateLegacyKinds(document.Root, notes), registry, notes) ?? FallbackRoot(registry, notes);

		// 2. Inject missing default panes unless the user explicitly dismissed them.
		var present = new HashSet<string>(StringComparer.Ordinal);
		CollectKinds(root, present);
		var dismissed = new HashSet<string>(document.Dismissed, StringComparer.Ordinal);
		foreach (var pane in registry.All) {
			if (pane.ShowByDefault
				&& !dismissed.Contains(pane.Kind)
				&& !present.Contains(pane.Kind)) {
				root = InjectAt(root, pane, notes);
				present.Add(pane.Kind);
			}
		}

		// 3. Bump the watermark, and 4. normalize weights + repair focus.
		root = Normalize(root);
		int seen = Math.Max(document.SeenPaneLevel, registry.CurrentPaneLevel);

		var ids = new HashSet<string>(StringComparer.Ordinal);
		CollectIds(root, ids);
		string? focused = document.Focused is not null && ids.Contains(document.Focused)
			? document.Focused
			: FirstPaneId(root);

		var reconciled = document with {
			Root = root,
			SeenPaneLevel = seen,
			Focused = focused,
			// Re-adding a pane clears its dismissal tombstone.
			Dismissed = [.. document.Dismissed.Where(k => !present.Contains(k))],
		};

		bool mutated = !string.Equals(
			LayoutSerialization.Serialize(document), LayoutSerialization.Serialize(reconciled), StringComparison.Ordinal);
		return new ReconcileOutcome(reconciled, mutated, notes);
	}

	private static LayoutNode? Prune(LayoutNode node, PaneRegistry registry, List<string> notes) {
		switch (node) {
			case PaneNode pane:
				if (registry.IsKnown(pane.Kind)) {
					return pane;
				}

				notes.Add($"pruned unknown pane kind '{pane.Kind}'");
				return null;
			case SplitNode split:
				var children = new List<LayoutNode>();
				var weights = new List<double>();
				for (int i = 0; i < split.Children.Count; i++) {
					var child = Prune(split.Children[i], registry, notes);
					if (child is not null) {
						children.Add(child);
						weights.Add(i < split.Weights.Count ? split.Weights[i] : 1.0);
					}
				}

				return children.Count switch {
					0 => null,
					1 => children[0],
					_ => split with { Children = children, Weights = weights },
				};
			default:
				return node;
		}
	}

	private static LayoutNode MigrateLegacyKinds(LayoutNode node, List<string> notes) {
		switch (node) {
			case PaneNode pane when pane.Kind == LayoutPanes.Agent:
				notes.Add("migrated pane kind 'agent' to 'terminal:claude'");
				return pane with { Kind = LayoutPanes.TerminalClaude };
			case SplitNode split:
				return split with { Children = [.. split.Children.Select(child => MigrateLegacyKinds(child, notes))] };
			default:
				return node;
		}
	}

	private static LayoutNode InjectAt(LayoutNode root, PaneDefinition pane, List<string> notes) {
		var leaf = new PaneNode { Id = NewId(pane.Kind), Kind = pane.Kind };
		notes.Add($"injected new default pane '{pane.Kind}'");
		return pane.DefaultAnchor switch {
			PaneAnchor.FarRight => new SplitNode {
				Dir = SplitDirection.Row,
				Weights = [1 - InjectWeight, InjectWeight],
				Children = [root, leaf],
			},
			PaneAnchor.LeftTop => InjectLeft(root, leaf, top: true),
			PaneAnchor.LeftBottom => InjectLeft(root, leaf, top: false),
			PaneAnchor.Top => new SplitNode {
				Dir = SplitDirection.Column,
				Weights = [InjectWeight, 1 - InjectWeight],
				Children = [leaf, root],
			},
			PaneAnchor.Bottom => new SplitNode {
				Dir = SplitDirection.Column,
				Weights = [1 - InjectWeight, InjectWeight],
				Children = [root, leaf],
			},
			// FarLeft lands on the left; Main only seeds an empty tree, so with a non-empty tree it falls
			// through to the same left placement.
			_ => new SplitNode {
				Dir = SplitDirection.Row,
				Weights = [InjectWeight, 1 - InjectWeight],
				Children = [leaf, root],
			},
		};
	}

	private static LayoutNode InjectLeft(LayoutNode root, PaneNode leaf, bool top) {
		if (root is SplitNode { Dir: SplitDirection.Row, Children.Count: > 1 } row) {
			var children = row.Children.ToArray();
			children[0] = Stack(children[0], leaf, top);
			return row with { Children = children };
		}

		return Stack(root, leaf, top);
	}

	private static SplitNode Stack(LayoutNode existing, PaneNode leaf, bool top) =>
		new() {
			Dir = SplitDirection.Column,
			Weights = top ? [InjectWeight, 1 - InjectWeight] : [1 - InjectWeight, InjectWeight],
			Children = top ? [leaf, existing] : [existing, leaf],
		};

	private static LayoutNode Normalize(LayoutNode node) {
		if (node is not SplitNode split) {
			return node;
		}

		List<LayoutNode> children = [.. split.Children.Select(Normalize)];
		var weights = new List<double>(split.Weights);
		while (weights.Count < children.Count) {
			weights.Add(1.0);
		}

		if (weights.Count > children.Count) {
			weights = [.. weights.Take(children.Count)];
		}

		for (int i = 0; i < weights.Count; i++) {
			if (double.IsNaN(weights[i]) || weights[i] <= 0) {
				weights[i] = MinWeight;
			}
		}

		double sum = weights.Sum();
		for (int i = 0; i < weights.Count; i++) {
			weights[i] = sum > 0 ? weights[i] / sum : 1.0 / weights.Count;
		}

		return split with { Children = children, Weights = weights };
	}

	private static LayoutNode FallbackRoot(PaneRegistry registry, List<string> notes) {
		notes.Add("layout had no renderable panes; restored the default");
		return LayoutPanes.Default(registry).Root;
	}

	private static void CollectKinds(LayoutNode node, HashSet<string> into) {
		switch (node) {
			case PaneNode pane:
				into.Add(pane.Kind);
				break;
			case SplitNode split:
				foreach (var child in split.Children) {
					CollectKinds(child, into);
				}

				break;
		}
	}

	private static void CollectIds(LayoutNode node, HashSet<string> into) {
		switch (node) {
			case PaneNode pane:
				into.Add(pane.Id);
				break;
			case SplitNode split:
				foreach (var child in split.Children) {
					CollectIds(child, into);
				}

				break;
		}
	}

	private static string? FirstPaneId(LayoutNode node) =>
		node switch {
			PaneNode pane => pane.Id,
			SplitNode split => split.Children.Select(FirstPaneId).FirstOrDefault(static id => id is not null),
			_ => null,
		};

	private static string NewId(string kind) =>
		"p_" + string.Concat(kind.Select(static ch => char.IsLetterOrDigit(ch) ? ch : '_'));
}
