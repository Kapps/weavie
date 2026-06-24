using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="LayoutStore"/> over the in-memory filesystem: seed-on-first-run, persist + reload, the
/// Changed-vs-silent split between pane and window edits, malformed-file backup + reset, dismissal,
/// unknown-kind rejection, and serialization round-trip.
/// </summary>
public sealed class LayoutStoreTests {
	private const string LayoutPath = "/weavie-layout-tests/layout.json";

	private static LayoutStore NewStore(InMemoryFileSystem fs) =>
		new(fs, LayoutPanes.CreateRegistry(), LayoutPath);

	[Fact]
	public void FreshStore_SeedsAndPersistsDefault() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);

		Assert.True(fs.FileExists(LayoutPath));
		Assert.Equal("p_editor", store.Current.Focused);
	}

	[Fact]
	public void SetPanes_PersistsAndRaisesChanged() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);
		LayoutChange? seen = null;
		store.Subscribe(c => seen = c);

		var root = new SplitNode {
			Dir = SplitDirection.Row,
			Weights = [0.3, 0.7],
			Children = [
				new PaneNode { Id = "p_shell", Kind = LayoutPanes.TerminalShell },
				new PaneNode { Id = "p_editor", Kind = LayoutPanes.Editor },
			],
		};
		var result = store.SetPanes(root, "p_editor", LayoutSource.Mcp);

		Assert.True(result.Applied);
		Assert.NotNull(seen);
		Assert.Equal(LayoutSource.Mcp, seen!.Value.Source);

		var reloaded = NewStore(fs);
		Assert.Equal("p_editor", reloaded.Current.Focused);
	}

	[Fact]
	public void SetPanes_UnknownKind_Throws() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);
		var root = new PaneNode { Id = "x", Kind = "bogus:kind" };

		Assert.Throws<LayoutValidationException>(() => store.SetPanes(root, null, LayoutSource.Mcp));
	}

	[Fact]
	public void SetPanes_NoPanes_Throws() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);
		var empty = new SplitNode { Dir = SplitDirection.Row, Weights = [], Children = [] };

		var ex = Assert.Throws<LayoutValidationException>(() => store.SetPanes(empty, null, LayoutSource.Mcp));
		Assert.Contains("at least one pane", ex.Message);
	}

	[Fact]
	public void SetWindow_PersistsButDoesNotRaiseChanged() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);
		bool raised = false;
		store.Subscribe(_ => raised = true);

		store.SetWindow(new WindowState { X = 100, Y = 50, Width = 1400, Height = 900, Maximized = true });

		Assert.False(raised);
		var reloaded = NewStore(fs);
		Assert.NotNull(reloaded.Current.Window);
		Assert.Equal(1400, reloaded.Current.Window!.Width);
		Assert.True(reloaded.Current.Window.Maximized);
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(LayoutPath, "{ this is not valid json ");

		var store = new LayoutStore(fs, LayoutPanes.CreateRegistry(), LayoutPath);

		Assert.True(fs.FileExists(LayoutPath + ".bad"));
		Assert.Equal("p_editor", store.Current.Focused);
	}

	[Fact]
	public void DismissPane_RemovesTombstonesAndSurvivesReload() {
		var fs = new InMemoryFileSystem();
		var store = NewStore(fs);

		store.DismissPane(LayoutPanes.TerminalShell, LayoutSource.User);

		Assert.Contains(LayoutPanes.TerminalShell, store.Current.Dismissed);
		Assert.DoesNotContain(LayoutPanes.TerminalShell, KindsOf(store.Current.Root));

		var reloaded = NewStore(fs);
		Assert.DoesNotContain(LayoutPanes.TerminalShell, KindsOf(reloaded.Current.Root));
	}

	[Fact]
	public void Serialization_RoundTrips() {
		var registry = LayoutPanes.CreateRegistry();
		var doc = LayoutPanes.Default(registry) with {
			Window = new WindowState { X = 1, Y = 2, Width = 3, Height = 4 },
		};

		string json = LayoutSerialization.Serialize(doc);
		Assert.True(LayoutSerialization.TryDeserialize(json, out var back, out _));
		Assert.Equal(json, LayoutSerialization.Serialize(back!));
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
