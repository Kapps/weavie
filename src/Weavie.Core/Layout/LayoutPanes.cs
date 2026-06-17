using Weavie.Core.FileSystem;

namespace Weavie.Core.Layout;

/// <summary>
/// Registers Weavie's built-in pane kinds and builds the default layout — the layout-system analogue of
/// <see cref="Weavie.Core.Configuration.CoreSettings"/>. Shipping a new default pane is a single
/// <see cref="PaneRegistry.Register"/> call with a higher <see cref="PaneDefinition.IntroducedIn"/>; the
/// reconciler then makes it appear for existing users exactly once.
/// </summary>
public static class LayoutPanes {
	/// <summary>Pane kind for the Monaco editor.</summary>
	public const string Editor = "editor";

	/// <summary>Pane kind for the embedded Claude Code session.</summary>
	public const string TerminalClaude = "terminal:claude";

	/// <summary>Pane kind for the plain shell terminal.</summary>
	public const string TerminalShell = "terminal:shell";

	/// <summary>Builds a registry pre-loaded with the built-in pane kinds.</summary>
	public static PaneRegistry CreateRegistry() {
		var registry = new PaneRegistry();
		Register(registry);
		return registry;
	}

	/// <summary>Creates a layout store backed by the built-in pane registry over <paramref name="filePath"/>.</summary>
	public static LayoutStore CreateStore(string? filePath = null) =>
		new(new LocalFileSystem(), CreateRegistry(), filePath);

	/// <summary>Registers the built-in pane kinds into <paramref name="registry"/>.</summary>
	public static void Register(PaneRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);

		registry.Register(new PaneDefinition {
			Kind = Editor,
			Description = "Code editor.",
			IntroducedIn = 1,
			DefaultAnchor = PaneAnchor.Main,
		});
		registry.Register(new PaneDefinition {
			Kind = TerminalClaude,
			Description = "Embedded Claude Code session.",
			IntroducedIn = 1,
			DefaultAnchor = PaneAnchor.LeftTop,
		});
		registry.Register(new PaneDefinition {
			Kind = TerminalShell,
			Description = "Plain shell terminal.",
			IntroducedIn = 1,
			DefaultAnchor = PaneAnchor.LeftBottom,
		});
	}

	/// <summary>
	/// The built-in default layout: a left column stacking the Claude and shell terminals beside the
	/// editor on the right (40/60), matching the legacy hardcoded arrangement. The seen-pane watermark is
	/// set to the current level so a brand-new user already has every current default.
	/// </summary>
	public static LayoutDocument Default(PaneRegistry registry) {
		ArgumentNullException.ThrowIfNull(registry);
		var root = new SplitNode {
			Dir = SplitDirection.Row,
			Weights = [0.4, 0.6],
			Children = [
				new SplitNode {
					Dir = SplitDirection.Column,
					Weights = [0.5, 0.5],
					Children = [
						new PaneNode { Id = "p_claude", Kind = TerminalClaude },
						new PaneNode { Id = "p_shell", Kind = TerminalShell },
					],
				},
				new PaneNode { Id = "p_editor", Kind = Editor },
			],
		};

		return new LayoutDocument {
			Version = 1,
			SeenPaneLevel = registry.CurrentPaneLevel,
			Focused = "p_editor",
			Root = root,
		};
	}
}
