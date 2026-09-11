using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// A session whose working directory is deleted out from under it ends, instead of staying on the rail and
/// reporting git's missing-directory words on every reconnect.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreVanishedWorktreeTests {
	// git's account of a directory that isn't there — the words this fix exists to stop republishing.
	private const string MissingDirectory = "Git working directory does not exist";

	private static IReadOnlyList<string?> SessionIds(TestHost host) {
		var list = host.Bridge.LastEvent("sessions", "catalog");
		return list is null ? [] : [.. list.Value.EnumerateArray().Select(s => s.GetProperty("id").GetString())];
	}

	private static string[] NotificationMessages(TestHost host) => [.. host.Bridge
		.PostedEvents("notifications", "show")
		.Select(notification => notification.GetProperty("message").GetString()!)];

	// An observer that fails while the deletion is still in flight reports as usual — the directory was there.
	private static void AssertReportedOnce(TestHost host, string expected) {
		string[] messages = NotificationMessages(host);
		Assert.Equal(1, messages.Count(message => message == expected));
		Assert.DoesNotContain(messages, message => message.Contains(MissingDirectory, StringComparison.Ordinal));
	}

	[Fact]
	public async Task DeletedWorktreeClosesItsSession() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		var session = host.Session("feature");
		string worktree = session.WorkspaceRoot;
		string closed = $"Session 'feature' was closed: its worktree {worktree} no longer exists.";
		host.Bridge.Clear();

		Directory.Delete(worktree, recursive: true);
		host.SessionEvent(session, "files", "refreshIndex", new { });

		await Wait.UntilAsync(() => NotificationMessages(host).Contains(closed));
		AssertReportedOnce(host, closed);
		Assert.DoesNotContain("feature", SessionIds(host));
	}

	[Fact]
	public async Task DeletedWorkspaceCheckoutReportsOnceAndKeepsItsSession() {
		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;
		string root = session.WorkspaceRoot;
		string gone = $"This workspace's folder no longer exists: {root}. Open a workspace that does.";
		host.Bridge.Clear();

		Directory.Delete(root, recursive: true);
		host.SessionEvent(session, "files", "refreshIndex", new { });
		await Wait.UntilAsync(() => NotificationMessages(host).Contains(gone));
		host.SessionEvent(session, "files", "refreshIndex", new { });
		await Wait.UntilAsync(() => host.Bridge.PostedEvents("files", "index").Count() > 1);

		AssertReportedOnce(host, gone);
		Assert.Same(session, host.WorkspaceSession);
	}
}
