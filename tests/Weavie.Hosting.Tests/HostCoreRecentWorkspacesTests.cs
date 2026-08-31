using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class HostCoreRecentWorkspacesTests {
	[Fact]
	public async Task Changed_PushesCurrentRecentsToExistingPage() {
		await using var host = await TestHost.StartAsync();

		host.Platform.SetRecents("/work/second", "/work/first");

		var pushed = Assert.Single(host.Bridge.PostedEvents("recentWorkspaces", "changed"));
		Assert.Equal(["/work/second", "/work/first"],
			pushed.GetProperty("recents").EnumerateArray().Select(item => item.GetString()));
	}
}
