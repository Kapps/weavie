using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Sessions;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The loaded/active overlay survives a worker restart (docs/specs/runner-auto-update.md §Recover): the
/// sessions that were live come back loaded (each --resumes), the last-active one comes back active, an
/// unloaded session stays unloaded, and a stale overlay naming a session that no longer reconciles falls back
/// to the primary without breaking startup. This is the regression behind an idle auto-update silently
/// unloading remote sessions. Requires <c>git</c> on PATH.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSessionRestoreTests {
	private static string Msg(object value) => JsonSerializer.Serialize(value);

	private static JsonElement SessionEntry(FakeHostBridge bridge, Func<JsonElement, bool> match) {
		var list = bridge.LastOfType("session-list");
		Assert.True(list.HasValue, "no session-list was pushed");
		foreach (var session in list!.Value.GetProperty("sessions").EnumerateArray()) {
			if (match(session)) {
				return session;
			}
		}

		throw new Xunit.Sdk.XunitException("no matching session in the list");
	}

	private static JsonElement SessionById(FakeHostBridge bridge, string id) =>
		SessionEntry(bridge, s => s.GetProperty("id").GetString() == id);

	private static bool PrimaryIsActive(FakeHostBridge bridge) =>
		SessionEntry(bridge, s => s.GetProperty("primary").GetBoolean()).GetProperty("active").GetBoolean();

	[Fact]
	public async Task LoadedAndActiveSessions_SurviveAWorkerRestart() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("branch-a")).Ok);
		Assert.True((await host.CreateSessionAsync("branch-b")).Ok); // branch-b is now active; branch-a stays loaded
		host.Send(Msg(new { type = "switch-session", id = "branch-a" })); // make branch-a active again

		await host.RestartAsync();

		// Pre-fix: both would return unloaded and the primary active. The overlay brings them back as they were.
		var a = SessionById(host.Bridge, "branch-a");
		var b = SessionById(host.Bridge, "branch-b");
		Assert.True(a.GetProperty("loaded").GetBoolean());
		Assert.True(a.GetProperty("active").GetBoolean());
		Assert.True(b.GetProperty("loaded").GetBoolean());
		Assert.False(b.GetProperty("active").GetBoolean());
	}

	[Fact]
	public async Task UnloadedSession_StaysUnloadedAfterRestart_WithPrimaryActive() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("branch-a")).Ok); // active + loaded
		Assert.True((await host.Core.UnloadSessionAsync("branch-a", CancellationToken.None)).Ok); // → dormant, primary active

		await host.RestartAsync();

		var a = SessionById(host.Bridge, "branch-a");
		Assert.False(a.GetProperty("loaded").GetBoolean()); // no spurious reload
		Assert.False(a.GetProperty("active").GetBoolean());
		Assert.True(PrimaryIsActive(host.Bridge));
	}

	[Fact]
	public async Task CodexSession_RestoresAsCodexAfterRestart() {
		await using var host = await TestHost.StartAsync();
		var result = await host.Core.NewSessionAsync(new NewSessionRequest {
			Branch = "codex-branch",
			Base = "main",
			AgentProviderId = "codex",
		}, CancellationToken.None);
		Assert.True(result.Ok);

		await host.RestartAsync();

		var session = SessionById(host.Bridge, "codex-branch");
		Assert.Equal("codex", session.GetProperty("providerId").GetString());
		Assert.True(session.GetProperty("loaded").GetBoolean());
		Assert.True(session.GetProperty("active").GetBoolean());
	}

	[Fact]
	public async Task CodexWorktree_RestoresProvider_WhenSessionOverlayIsMissing() {
		await using var host = await TestHost.StartAsync();
		var result = await host.Core.NewSessionAsync(new NewSessionRequest {
			Branch = "codex-branch",
			Base = "main",
			AgentProviderId = "codex",
		}, CancellationToken.None);
		Assert.True(result.Ok);
		string overlay = WeaviePaths.WorkspaceSessionsFile(WorkspaceId.ForPath(host.RepoRoot));

		await host.RestartAsync(() => File.WriteAllText(overlay, """{"version":1,"activeId":null,"sessions":[]}"""));

		var session = SessionById(host.Bridge, "codex-branch");
		Assert.Equal("codex", session.GetProperty("providerId").GetString());
		Assert.False(session.GetProperty("loaded").GetBoolean());
		Assert.False(session.GetProperty("active").GetBoolean());
	}

	[Fact]
	public async Task StaleOverlayNamingAMissingSlot_IsSkipped_PrimaryStaysActive() {
		await using var host = await TestHost.StartAsync();

		// An overlay naming a session whose worktree no longer reconciles (removed out-of-band). Restore must skip
		// it — the live git set wins over the stored overlay — and leave the primary active without throwing.
		string overlay = WeaviePaths.WorkspaceSessionsFile(WorkspaceId.ForPath(host.RepoRoot));
		Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
		File.WriteAllText(overlay,
			"""{"version":1,"activeId":"ghost","sessions":[{"id":"ghost","label":"ghost","worktreePath":"/gone","isPrimary":false,"loaded":true}]}""");

		await host.RestartAsync();

		Assert.True(PrimaryIsActive(host.Bridge));
		Assert.DoesNotContain(
			host.Bridge.LastOfType("session-list")!.Value.GetProperty("sessions").EnumerateArray(),
			s => s.GetProperty("id").GetString() == "ghost");
	}

	[Fact]
	public async Task BranchList_IncludesSurfacedSessionBranches_ForSwitching() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("branch-a")).Ok);
		host.Bridge.Clear();

		host.Send("""{"type":"list-branches","id":"branches-1"}""");

		var reply = await Wait.ForAsync(() => host.Bridge.LastOfType("branches-result"));
		Assert.Contains(
			"branch-a",
			reply.GetProperty("branches").EnumerateArray().Select(branch => branch.GetString()));
	}
}
