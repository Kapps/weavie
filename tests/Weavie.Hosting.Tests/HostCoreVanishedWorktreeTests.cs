using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// A session whose working directory is deleted out from under it ends, instead of staying on the rail and
/// reporting git's missing-directory words on every reconnect.
/// </summary>
// Flaked 2026-09-12 03:17 UTC on main (https://github.com/Kapps/weavie/actions/runs/34669658275/job/103488957262):
// DeletedWorkspaceCheckoutReportsOnceAndKeepsItsSession timed out waiting for the "gone" notification. Root cause:
// Directory.Delete(recursive) removes .git before the rest of the tree, so a refresh racing that deletion can see
// git's own "not a git repository" (WorkspaceInventory.RefreshAsync returns IsRepository=false without throwing)
// instead of a launch failure. PushFileIndexToWeb only asked EndIfWorkspaceRootIsGone from its exception handler,
// so this success path never noticed the root was gone — and once WorkspaceInventory caches "not a repository" it
// never calls git again, so the session stayed silently stuck. Fixed by also checking the vanished-root fact on
// the non-repository success path in HostCore.WebBridge.cs, not just on git exceptions. Reproduced locally ~1 in
// 15-20 runs before the fix, 0 failures in 400 repeats after.
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
