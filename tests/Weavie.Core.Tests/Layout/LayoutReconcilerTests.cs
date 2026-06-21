using Weavie.Core.Layout;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Forward/backward-compatibility core: prune removed kinds, inject a newly-introduced default once,
/// respect dismissals, normalize weights, repair focus, and stay idempotent.
/// </summary>
public sealed class LayoutReconcilerTests {
	private static PaneRegistry BaseRegistry() => LayoutPanes.CreateRegistry();

	private static PaneRegistry RegistryWithFileTree() {
		var registry = LayoutPanes.CreateRegistry();
		registry.Register(new PaneDefinition {
			Kind = "fileTree",
			Description = "File tree.",
			IntroducedIn = 2,
			DefaultAnchor = PaneAnchor.FarLeft,
		});
		return registry;
	}

	[Fact]
	public void Reconcile_IsIdempotent() {
		var registry = BaseRegistry();
		var once = LayoutReconciler.Reconcile(LayoutPanes.Default(registry), registry);
		Assert.Empty(once.Notes);

		var twice = LayoutReconciler.Reconcile(once.Document, registry);
		Assert.False(twice.Mutated);
	}

	[Fact]
	public void PrunesUnknownKind_AndCollapsesSplit() {
		var registry = BaseRegistry();
		var root = new SplitNode {
			Dir = SplitDirection.Row,
			Weights = [0.5, 0.5],
			Children = [
				new PaneNode { Id = "p_editor", Kind = LayoutPanes.Editor },
				new PaneNode { Id = "p_ghost", Kind = "ghost:removed" },
			],
		};
		var doc = new LayoutDocument { SeenPaneLevel = registry.CurrentPaneLevel, Root = root };

		var outcome = LayoutReconciler.Reconcile(doc, registry);

		Assert.True(outcome.Mutated);
		Assert.Contains(outcome.Notes, n => n.Contains("ghost:removed"));
		var pane = Assert.IsType<PaneNode>(outcome.Document.Root);
		Assert.Equal(LayoutPanes.Editor, pane.Kind);
	}

	[Fact]
	public void InjectsNewlyIntroducedDefault_Once() {
		var registry = RegistryWithFileTree();
		var doc = LayoutPanes.Default(BaseRegistry()) with { SeenPaneLevel = 1 };

		var outcome = LayoutReconciler.Reconcile(doc, registry);

		Assert.True(outcome.Mutated);
		Assert.Equal(2, outcome.Document.SeenPaneLevel);
		Assert.Contains("fileTree", KindsOf(outcome.Document.Root));

		// Already seen + present: reconciling again is a no-op.
		var again = LayoutReconciler.Reconcile(outcome.Document, registry);
		Assert.False(again.Mutated);
	}

	[Fact]
	public void DismissedKind_NotInjected_ButWatermarkStillBumps() {
		var registry = RegistryWithFileTree();
		var doc = LayoutPanes.Default(BaseRegistry()) with { SeenPaneLevel = 1, Dismissed = ["fileTree"] };

		var outcome = LayoutReconciler.Reconcile(doc, registry);

		Assert.DoesNotContain("fileTree", KindsOf(outcome.Document.Root));
		Assert.Equal(2, outcome.Document.SeenPaneLevel);
		Assert.Contains("fileTree", outcome.Document.Dismissed);
	}

	[Fact]
	public void NormalizesWeights_ToSumToOne() {
		var registry = BaseRegistry();
		var root = new SplitNode {
			Dir = SplitDirection.Row,
			Weights = [3, 1],
			Children = [
				new PaneNode { Id = "a", Kind = LayoutPanes.Editor },
				new PaneNode { Id = "b", Kind = LayoutPanes.TerminalShell },
			],
		};
		var doc = new LayoutDocument { SeenPaneLevel = registry.CurrentPaneLevel, Root = root };

		var outcome = LayoutReconciler.Reconcile(doc, registry);

		var split = Assert.IsType<SplitNode>(outcome.Document.Root);
		Assert.Equal(1.0, split.Weights.Sum(), 3);
		Assert.Equal(0.75, split.Weights[0], 3);
	}

	[Fact]
	public void RepairsFocus_WhenPointingAtMissingPane() {
		var registry = BaseRegistry();
		var doc = LayoutPanes.Default(registry) with { Focused = "does_not_exist" };

		var outcome = LayoutReconciler.Reconcile(doc, registry);

		Assert.NotNull(outcome.Document.Focused);
		Assert.NotEqual("does_not_exist", outcome.Document.Focused);
	}

	private static List<string> KindsOf(LayoutNode node) {
		var list = new List<string>();
		Collect(node, list);
		return list;

		static void Collect(LayoutNode current, List<string> into) {
			switch (current) {
				case PaneNode pane:
					into.Add(pane.Kind);
					break;
				case SplitNode split:
					foreach (var child in split.Children) {
						Collect(child, into);
					}

					break;
			}
		}
	}
}
