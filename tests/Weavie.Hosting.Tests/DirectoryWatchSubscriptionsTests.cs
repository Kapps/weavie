using System.Text.Json;
using Weavie.Core.FileActivity;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class DirectoryWatchSubscriptionsTests {
	[Fact]
	public void DisconnectReleasesOnlyItsPeersSubscriptionsAndRejectsLateRequests() {
		using var root = new TempDirectory("weavie-listings");
		using var watcher = NewWatcher();
		using var first = new DirectoryWatchSubscriptions(watcher);
		using var second = new DirectoryWatchSubscriptions(watcher);
		first.Watch("listing", root.Path);
		second.Watch("listing", root.Path);

		first.Dispose();
		first.Unwatch("listing");
		Assert.Equal(1, watcher.WatchedDirectoryCount);
		Assert.Throws<ObjectDisposedException>(() => first.Watch("late", root.Path));

		second.Unwatch("listing");
		Assert.Equal(0, watcher.WatchedDirectoryCount);
	}

	[Fact]
	public void PageReloadReleasesPreviousEpochAndKeepsOtherPeersSubscriptions() {
		using var root = new TempDirectory("weavie-listings");
		using var watcher = NewWatcher();
		using var first = new DirectoryWatchSubscriptions(watcher);
		using var second = new DirectoryWatchSubscriptions(watcher);
		first.Reset("page-one");
		first.Watch("old-listing", root.Path);
		second.Watch("listing", root.Path);

		first.Reset("page-one");
		second.Unwatch("listing");
		Assert.Equal(1, watcher.WatchedDirectoryCount);
		second.Watch("listing", root.Path);
		first.Reset("page-two");
		Assert.Equal(1, watcher.WatchedDirectoryCount);
		second.Unwatch("listing");
		Assert.Equal(0, watcher.WatchedDirectoryCount);

		first.Watch("new-listing", root.Path);
		first.Unwatch("old-listing");
		Assert.Equal(1, watcher.WatchedDirectoryCount);
	}

	[Fact]
	public async Task ListingMessagesOwnWatchesAndReturnCanonicalPaths() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		host.SessionEvent(session, "files", "reset", new { pageEpoch = "first-page" });
		var first = await host.SessionRequestAsync<JsonElement>(session, "files", "listDirectory",
			new { subscriptionId = "browser", path = host.RepoRoot });
		var second = await host.SessionRequestAsync<JsonElement>(session, "files", "listDirectory",
			new { subscriptionId = "completion", path = Path.Combine(host.RepoRoot, ".") + Path.DirectorySeparatorChar });
		Assert.Equal(PathIdentity.Normalize(host.RepoRoot), second.GetProperty("path").GetString());
		Assert.Equal(first.GetProperty("entries").GetRawText(), second.GetProperty("entries").GetRawText());

		host.SessionEvent(session, "files", "unwatchDirectory", new { subscriptionId = "browser" });
		Assert.Equal(1, session.ObservedPaths.WatchedDirectoryCount);
		host.SessionEvent(session, "files", "reset", new { pageEpoch = "second-page" });
		Assert.Equal(0, session.ObservedPaths.WatchedDirectoryCount);
	}

	[Fact]
	public async Task TransportDisconnectReleasesItsListings() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		await host.SessionRequestAsync<JsonElement>(session, "files", "listDirectory",
			new { subscriptionId = "browser", path = host.RepoRoot });
		Assert.Equal(1, session.ObservedPaths.WatchedDirectoryCount);

		host.Bridge.Disconnect(new WebPeer(TestHost.TestPageId));

		await Wait.UntilAsync(() => session.ObservedPaths.WatchedDirectoryCount == 0);
	}

	private static ObservedPathWatcher NewWatcher() =>
		new(new LocalFileSystem(), NoopFileActivitySink.Instance, Assert.Fail, debounceMs: 10);
}
