using System.Text.Json;
using Weavie.Core.FileSystem;
using Weavie.Core.Layout;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class ToolLayoutTests {
	private const string Path = "/tool-layout/layout.json";
	private static LayoutStore Store(InMemoryFileSystem fs) => new(fs, LayoutPanes.CreateRegistry(), Path);
	private static string Json(LayoutNode root) => JsonSerializer.Serialize(root, LayoutSerialization.Options);

	[Fact]
	public void LayoutNodesUseTheSameShapeInTypedWebRequests() {
		var root = ToolLayout.Apply(Store(new InMemoryFileSystem()).Current.Root, LayoutPanes.Files, "dock");
		string wire = JsonSerializer.Serialize(root, JsonSerializerOptions.Web);
		Assert.Contains("\"dir\":\"row\"", wire);
		var parsed = JsonSerializer.Deserialize<LayoutNode>(wire, JsonSerializerOptions.Web);
		Assert.Equal(Json(root), Json(parsed!));
	}

	[Theory]
	[InlineData(LayoutPanes.Files, LayoutPanes.Search)]
	[InlineData(LayoutPanes.Search, LayoutPanes.Files)]
	public void DockingStacksToolsWithoutChangingPrimaryTree(string first, string second) {
		var store = Store(new InMemoryFileSystem());
		string primary = Json(store.Current.Root);
		store.ChangeTool(first, "dock");
		store.ChangeTool(second, "dock");
		var row = Assert.IsType<SplitNode>(store.Current.Root);
		Assert.Equal(SplitDirection.Row, row.Dir);
		Assert.Equal(primary, Json(row.Children[1]));
		var column = Assert.IsType<SplitNode>(row.Children[0]);
		Assert.Equal(SplitDirection.Column, column.Dir);
		Assert.Equal(LayoutPanes.Files, Assert.IsType<PaneNode>(column.Children[0]).Kind);
		Assert.Equal(LayoutPanes.Search, Assert.IsType<PaneNode>(column.Children[1]).Kind);
		store.ChangeTool(first, "float");
		store.ChangeTool(second, "float");
		Assert.Equal(primary, Json(store.Current.Root));
	}

	[Fact]
	public void HiddenDockRetainsResizedGeometryAcrossReloadAndReopen() {
		var fs = new InMemoryFileSystem();
		var store = Store(fs);
		store.ChangeTool(LayoutPanes.Files, "dock");
		store.ChangeTool(LayoutPanes.Search, "dock");
		var row = Assert.IsType<SplitNode>(store.Current.Root);
		var column = Assert.IsType<SplitNode>(row.Children[0]);
		store.Resize(row, row with {
			Weights = [0.3, 0.7],
			Children = [column with { Weights = [0.6, 0.4] }, row.Children[1]],
		});
		string visible = Json(store.Current.Root);
		store.ChangeTool(LayoutPanes.Files, "hide");
		store.ChangeTool(LayoutPanes.Search, "hide");
		var reloaded = Store(fs);
		Assert.Equal(Json(store.Current.Root), Json(reloaded.Current.Root));
		var hiddenRow = Assert.IsType<SplitNode>(reloaded.Current.Root);
		var hiddenColumn = Assert.IsType<SplitNode>(hiddenRow.Children[0]);
		Assert.All(hiddenColumn.Children, child => Assert.True(Assert.IsType<PaneNode>(child).Hidden));
		reloaded.ChangeTool(LayoutPanes.Files, "show");
		reloaded.ChangeTool(LayoutPanes.Search, "show");
		Assert.Equal(visible, Json(reloaded.Current.Root));
	}

	[Fact]
	public void StaleResizeCannotUndoDockingOrBroadcastAChange() {
		var store = Store(new InMemoryFileSystem());
		var beforeDock = Assert.IsType<SplitNode>(store.Current.Root);
		store.ChangeTool(LayoutPanes.Files, "dock");
		string docked = Json(store.Current.Root);
		int changes = 0;
		store.Changed += _ => changes++;
		Assert.Throws<LayoutValidationException>(() =>
			store.Resize(beforeDock, beforeDock with { Weights = [0.3, 0.7] }));
		Assert.Equal(docked, Json(store.Current.Root));
		Assert.Equal(0, changes);
	}

	[Theory]
	[InlineData(LayoutPanes.Editor, "dock")]
	[InlineData(LayoutPanes.Files, "unknown")]
	public void InvalidMutationLeavesDocumentUnchanged(string kind, string action) {
		var store = Store(new InMemoryFileSystem());
		string original = Json(store.Current.Root);
		Assert.Throws<LayoutValidationException>(() => store.ChangeTool(kind, action));
		Assert.Equal(original, Json(store.Current.Root));
	}
}
