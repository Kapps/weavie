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

	// Flaked 2026-09-12 03:56 UTC (https://github.com/Kapps/weavie/actions/runs/34671553854): timed out waiting
	// for the "gone" notification. Root cause: connect's own "lifecycle"/"sync" kicks off a file-index refresh
	// in the background (unawaited) and this test used to delete the workspace root right after StartAsync
	// returned, before that refresh's real `git` subprocess necessarily had a chance to run. That refresh holds
	// FileIndexGate until it completes, so this test's own delete-triggered refresh queued behind it — normally
	// for a few milliseconds, but occasionally longer under contended CI, pushing the 5s wait past the deadline.
	// Fixed by waiting for that initial refresh's own completion signal (a non-pending "files","index" event)
	// before touching the workspace, so the test no longer races an unrelated background task's subprocess.
	[Fact]
	public async Task DeletedWorkspaceCheckoutReportsOnceAndKeepsItsSession() {
		await using var host = await TestHost.StartAsync();
		var session = host.WorkspaceSession;
		string root = session.WorkspaceRoot;
		string gone = $"This workspace's folder no longer exists: {root}. Open a workspace that does.";

		// Connect's "lifecycle"/"sync" triggers its own file-index refresh in the background (unawaited); wait for
		// it to land so it isn't still holding FileIndexGate when this test triggers its own refresh below.
		await Wait.UntilAsync(() => host.Bridge.PostedEvents("files", "index")
			.Any(index => !index.GetProperty("pending").GetBoolean()));
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
