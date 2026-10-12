using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="RailStateStore"/> over the in-memory filesystem: defaults, last-location + selected
/// session + panel layout persistence across reloads, no-op-on-unchanged (no rewrite, no event), change
/// notifications, and malformed-file backup + reset.
/// </summary>
public sealed class RailStateStoreTests {
	private const string StorePath = "/weavie-rail-state-tests/rail-state.json";

	[Fact]
	public void Defaults_LocalAndEmpty() {
		var store = new RailStateStore(new InMemoryFileSystem(), StorePath);

		Assert.Equal("local", store.LastLocation);
		Assert.Null(store.Selected);
		Assert.False(store.PanelPinned);
		Assert.True(store.ParkedCollapsed);
	}

	[Fact]
	public void SetLastLocation_PersistsAcrossReload() {
		var fs = new InMemoryFileSystem();
		new RailStateStore(fs, StorePath).SetLastLocation("remote:devbox");

		Assert.Equal("remote:devbox", new RailStateStore(fs, StorePath).LastLocation);
	}

	[Fact]
	public void SetPanelPinned_PersistsAcrossReload_AndNotifiesOnlyOnChange() {
		var fs = new InMemoryFileSystem();
		var store = new RailStateStore(fs, StorePath);
		int changes = 0;
		store.Changed += () => changes++;

		store.SetPanelPinned(true);
		store.SetPanelPinned(true);

		Assert.Equal(1, changes);
		Assert.True(new RailStateStore(fs, StorePath).PanelPinned);
	}

	[Fact]
	public void SetPanelWidth_PersistsAcrossReload() {
		var fs = new InMemoryFileSystem();
		Assert.Equal(RailStateStore.DefaultPanelWidth, new RailStateStore(fs, StorePath).PanelWidth);

		new RailStateStore(fs, StorePath).SetPanelWidth(240);

		Assert.Equal(240, new RailStateStore(fs, StorePath).PanelWidth);
	}

	[Fact]
	public void SetLastLocation_BlankFallsBackToLocal() {
		var store = new RailStateStore(new InMemoryFileSystem(), StorePath);
		store.SetLastLocation("remote:devbox");

		store.SetLastLocation("   ");

		Assert.Equal("local", store.LastLocation);
	}

	[Fact]
	public void SetSelected_PersistsAcrossReload() {
		var fs = new InMemoryFileSystem();
		new RailStateStore(fs, StorePath).SetSelected("remote:devbox", "feature");

		Assert.Equal(
			("remote:devbox", "feature"),
			new RailStateStore(fs, StorePath).Selected);
	}

	[Fact]
	public void SetSelected_UnchangedDoesNotRewriteOrNotify() {
		var fs = new InMemoryFileSystem();
		var store = new RailStateStore(fs, StorePath);
		store.SetSelected("local", "feature");
		string afterFirst = fs.ReadAllText(StorePath);
		int changes = 0;
		store.Changed += () => changes++;

		store.SetSelected("local", "feature");

		Assert.Equal(afterFirst, fs.ReadAllText(StorePath));
		Assert.Equal(0, changes);
	}

	[Fact]
	public void SetLastLocation_Unchanged_DoesNotRewriteOrNotify() {
		var fs = new InMemoryFileSystem();
		var store = new RailStateStore(fs, StorePath);
		store.SetLastLocation("remote:devbox");
		string afterFirst = fs.ReadAllText(StorePath);
		int changes = 0;
		store.Changed += () => changes++;

		store.SetLastLocation("remote:devbox");

		Assert.Equal(afterFirst, fs.ReadAllText(StorePath)); // identical bytes — no rewrite
		Assert.Equal(0, changes);
	}

	[Fact]
	public void Changed_FiresOnRealChange() {
		var store = new RailStateStore(new InMemoryFileSystem(), StorePath);
		int changes = 0;
		store.Changed += () => changes++;

		store.SetLastLocation("remote:devbox");
		store.SetParkedCollapsed(false);

		Assert.Equal(2, changes);
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, "{ broken ");

		var store = new RailStateStore(fs, StorePath);

		Assert.True(fs.FileExists(StorePath + ".bad"));
		Assert.Equal("local", store.LastLocation);
		Assert.True(store.ParkedCollapsed);
	}

	[Fact]
	public void SetParkedCollapsed_PersistsAcrossReload() {
		var fs = new InMemoryFileSystem();
		new RailStateStore(fs, StorePath).SetParkedCollapsed(false);

		Assert.False(new RailStateStore(fs, StorePath).ParkedCollapsed);
	}
}
