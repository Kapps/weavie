namespace Weavie.Core.Layout;

/// <summary>Placement and visibility operations for optional tool panes.</summary>
public static class ToolLayout {
	/// <summary>Applies a dock, float, show, or hide operation without changing the primary pane arrangement.</summary>
	public static LayoutNode Apply(LayoutNode root, string kind, string action) {
		if (kind is not (LayoutPanes.Files or LayoutPanes.Search)) {
			throw new LayoutValidationException($"Not a tool pane: {kind}");
		}
		return action switch {
			"dock" => Contains(root, kind) ? SetHidden(root, kind, false) : Dock(root, kind),
			"float" => LayoutTree.Filter(root, pane => pane.Kind != kind)
				?? throw new LayoutValidationException("Cannot float the only pane."),
			"show" => SetHidden(root, kind, false),
			"hide" => SetHidden(root, kind, true),
			_ => throw new LayoutValidationException($"Unknown tool action: {action}"),
		};
	}

	private static bool Contains(LayoutNode node, string kind) => node switch {
		PaneNode pane => pane.Kind == kind,
		SplitNode split => split.Children.Any(child => Contains(child, kind)),
		_ => false,
	};

	private static LayoutNode SetHidden(LayoutNode node, string kind, bool hidden) => node switch {
		PaneNode pane when pane.Kind == kind => pane with { Hidden = hidden },
		SplitNode split => split with { Children = [.. split.Children.Select(child => SetHidden(child, kind, hidden))] },
		_ => node,
	};

	private static LayoutNode Dock(LayoutNode root, string kind) {
		var pane = new PaneNode { Id = $"p_{kind}", Kind = kind };
		string other = kind == LayoutPanes.Files ? LayoutPanes.Search : LayoutPanes.Files;
		if (Contains(root, other)) {
			return BesideTool(root, pane, other);
		}
		return new SplitNode { Dir = SplitDirection.Row, Weights = [0.22, 0.78], Children = [pane, root] };
	}

	private static LayoutNode BesideTool(LayoutNode node, PaneNode added, string other) => node switch {
		PaneNode pane when pane.Kind == other => new SplitNode {
			Dir = SplitDirection.Column,
			Weights = [0.5, 0.5],
			Children = added.Kind == LayoutPanes.Files ? [added, pane] : [pane, added],
		},
		SplitNode split => split with { Children = [.. split.Children.Select(child => BesideTool(child, added, other))] },
		_ => node,
	};
}
