using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// A session whose working directory is deleted out from under it ends, instead of staying on the rail and
/// reporting git's missing-directory words on every reconnect.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreVanishedWorktreeTests {
	private static IReadOnlyList<string?> SessionIds(TestHost host) {
		var list = host.Bridge.LastEvent("sessions", "catalog");
		return list is null ? [] : [.. list.Value.EnumerateArray().Select(s => s.GetProperty("id").GetString())];
	}

	private static string[] NotificationMessages(TestHost host) => [.. host.Bridge
		.PostedEvents("notifications", "show")
		.Select(notification => notification.GetProperty("message").GetString()!)];

	[Fact]
	public async Task DeletedWorktreeClosesItsSession() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		var session = host.Session("feature");
		string worktree = session.WorkspaceRoot;
		host.Bridge.Clear();

		Directory.Delete(worktree, recursive: true);
		host.SessionEvent(session, "files", "refreshIndex", new { });

		await Wait.UntilAsync(() => NotificationMessages(host).Length > 0);
		Assert.Equal(
			new[] { $"Session 'feature' was closed: its worktree {worktree} no longer exists." },
			NotificationMessages(host));
		Assert.DoesNotContain("feature", SessionIds(host));
	}

	[Fact]
	public async Task DeletedWorkspaceCheckoutReportsOnceAndKeepsItsSession() {
		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;
		string root = session.WorkspaceRoot;
		host.Bridge.Clear();

		Directory.Delete(root, recursive: true);
		host.SessionEvent(session, "files", "refreshIndex", new { });
		await Wait.UntilAsync(() => NotificationMessages(host).Length > 0);
		host.SessionEvent(session, "files", "refreshIndex", new { });
		await Wait.UntilAsync(() => host.Bridge.PostedEvents("files", "index").Count() > 1);

		Assert.Equal(
			new[] { $"This workspace's folder no longer exists: {root}. Open a workspace that does." },
			NotificationMessages(host));
		Assert.Same(session, host.WorkspaceSession);
	}
}
