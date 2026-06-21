using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// <see cref="WorkspaceManager"/>: a first open bumps recents, a repeat open of the same folder reports
/// already-open without growing the open set, close frees the slot, and distinct folders track
/// independently.
/// </summary>
public sealed class WorkspaceManagerTests {
	private static WorkspaceManager NewManager() =>
		new(new RecentWorkspaces(new InMemoryFileSystem(), "/weavie-wsmgr-tests/recents.json"));

	[Fact]
	public void Open_NewWorkspace_IsNotAlreadyOpen_AndRecorded() {
		var manager = NewManager();

		var result = manager.Open("/ws/a");

		Assert.False(result.AlreadyOpen);
		Assert.Equal(WorkspaceId.ForPath("/ws/a"), result.Id);
		Assert.Equal(1, manager.OpenCount);
		Assert.Equal(Path.GetFullPath("/ws/a"), manager.Recents.LastOpened);
	}

	[Fact]
	public void Open_SameWorkspaceTwice_SecondIsAlreadyOpen() {
		var manager = NewManager();
		manager.Open("/ws/a");

		var second = manager.Open("/ws/a");

		Assert.True(second.AlreadyOpen);
		Assert.Equal(1, manager.OpenCount);
	}

	[Fact]
	public void Open_DifferentPathSameFolder_DedupesByNormalizedId() {
		var manager = NewManager();
		manager.Open("/ws/a");

		var second = manager.Open("/ws/a/");

		Assert.True(second.AlreadyOpen);
		Assert.Equal(1, manager.OpenCount);
	}

	[Fact]
	public void Close_FreesTheSlot() {
		var manager = NewManager();
		var opened = manager.Open("/ws/a");

		manager.Close(opened.Id);

		Assert.Equal(0, manager.OpenCount);
		Assert.False(manager.IsOpen(opened.Id));
	}

	[Fact]
	public void Open_TwoDistinctWorkspaces_BothOpen() {
		var manager = NewManager();

		manager.Open("/ws/a");
		manager.Open("/ws/b");

		Assert.Equal(2, manager.OpenCount);
	}
}
