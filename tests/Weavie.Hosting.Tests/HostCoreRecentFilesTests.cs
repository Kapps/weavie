using Xunit;

namespace Weavie.Hosting.Tests;

[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreRecentFilesTests {
	[Fact]
	public async Task ActiveEditorRecordsOnlyFilesInsideItsCheckout() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		string inside = Path.Combine(host.RepoRoot, "readme.txt");
		host.Bridge.Clear();

		host.SessionEvent(session, "editor", "activeChanged", new { path = inside });

		Assert.Equal(inside, session.Editor.Active?.FilePath);
		var changed = Assert.Single(host.Bridge.PostedEvents("recentFiles", "changed"));
		Assert.Equal("readme.txt", Assert.Single(changed.GetProperty("files").EnumerateArray()).GetString());

		host.Bridge.Clear();
		host.SessionEvent(
			session,
			"editor",
			"activeChanged",
			new { path = Path.Combine(Path.GetDirectoryName(host.RepoRoot)!, "outside.txt") });

		Assert.Empty(host.Bridge.PostedEvents("recentFiles", "changed"));
	}
}
