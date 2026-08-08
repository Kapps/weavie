using System.Collections.Concurrent;
using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Weavie.Core.Workspaces;
using Weavie.Core.Worktrees;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The loaded-session overlay survives a worker restart. Selection is client-owned and is deliberately absent
/// from both persistence and the host catalog.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSessionRestoreTests {
	private static JsonElement SessionEntry(FakeHostBridge bridge, Func<JsonElement, bool> match) {
		var catalog = bridge.LastEvent("sessions", "catalog");
		Assert.True(catalog.HasValue, "no session catalog was published");
		foreach (var session in catalog!.Value.EnumerateArray()) {
			if (match(session)) {
				return session;
			}
		}

		throw new Xunit.Sdk.XunitException("no matching session in the list");
	}

	private static JsonElement SessionById(FakeHostBridge bridge, string id) =>
		SessionEntry(bridge, s => s.GetProperty("id").GetString() == id);

	[Fact]
	public async Task LoadedSessionsSurviveRestart_WhileClientSelectionResetsIndependently() {
		await using var host = await TestHost.StartAsync();
		var created = await host.CreateSessionAsync("branch-a");
		AssertAddress(created, host.Session("branch-a"));
		using (var data = JsonDocument.Parse(Assert.IsType<string>(created.DataJson))) {
			Assert.True(data.RootElement.GetProperty("activateSession").GetBoolean());
			Assert.True(data.RootElement.GetProperty("createdSession").GetBoolean());
		}
		Assert.True((await host.CreateSessionAsync("branch-b")).Ok);
		host.SelectSession("branch-a");

		await host.RestartAsync();

		var a = SessionById(host.Bridge, "branch-a");
		var b = SessionById(host.Bridge, "branch-b");
		Assert.True(a.GetProperty("loaded").GetBoolean());
		Assert.True(b.GetProperty("loaded").GetBoolean());
		Assert.False(a.TryGetProperty("active", out _));
		Assert.False(b.TryGetProperty("active", out _));
		Assert.Same(host.WorkspaceSession, host.SelectedSession);
	}

	[Fact]
	public async Task ReopeningAnExistingSession_ActivatesWithoutReportingCreation() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("branch-a")).Ok);
		var rejected = await host.InvokeClientCommandAsync(
			SessionCommands.NewSession,
			new { branch = "branch-a", existing = true, prompt = "Do not discard this" });
		Assert.False(rejected.Ok);
		Assert.Contains("prompt", rejected.Error, StringComparison.OrdinalIgnoreCase);
		var rejectedWorkspace = await host.InvokeClientCommandAsync(
			SessionCommands.NewSession,
			new { branch = host.WorkspaceSession.DisplayLabel, existing = true, prompt = "Keep this too" });
		Assert.False(rejectedWorkspace.Ok);
		Assert.Contains("prompt", rejectedWorkspace.Error, StringComparison.OrdinalIgnoreCase);

		var reopened = await host.HostRequestAsync<JsonElement>(
			"sessions",
			"invoke",
			new {
				id = SessionCommands.NewSession,
				args = new {
					branch = "branch-a",
					@base = "main",
					existing = true,
					prompt = "",
					attachments = Array.Empty<object>(),
					agentProviderId = "claude",
				},
			});

		Assert.True(reopened.GetProperty("ok").GetBoolean(), reopened.GetProperty("error").GetString());
		var data = reopened.GetProperty("data");
		Assert.True(data.GetProperty("activateSession").GetBoolean());
		Assert.False(data.TryGetProperty("createdSession", out _));
	}

	[Fact]
	public async Task UnloadedSession_StaysUnloadedAfterRestart() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("branch-a")).Ok);
		Assert.True((await host.UnloadSessionAsync("branch-a")).Ok);

		await host.RestartAsync();

		var a = SessionById(host.Bridge, "branch-a");
		Assert.False(a.GetProperty("loaded").GetBoolean());
		Assert.Same(host.WorkspaceSession, host.SelectedSession);
	}

	[Fact]
	public async Task LoadReturnsTheExactLiveAddressWhetherTheSessionWasDormantOrAlreadyLoaded() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("branch-a")).Ok);
		host.SelectWorkspaceSession();

		var alreadyLoaded = await host.InvokeClientCommandAsync(
			SessionCommands.LoadSession,
			new { id = "branch-a" });
		AssertAddress(alreadyLoaded, host.Session("branch-a"));

		Assert.True((await host.UnloadSessionAsync("branch-a")).Ok);
		var loaded = await host.InvokeClientCommandAsync(
			SessionCommands.LoadSession,
			new { id = "branch-a" });
		AssertAddress(loaded, host.Session("branch-a"));
	}

	[Fact]
	public async Task SelectedSessionCanReplyBeforeUnloadingItsOwnMessageBus() {
		var dispatcherErrors = new ConcurrentQueue<Exception>();
		await using var host = TestHost.CreateUnstarted(new SerialUiDispatcher(dispatcherErrors.Enqueue));
		await host.Core.StartAsync();
		await host.ConnectAsync();
		Assert.True((await host.CreateSessionAsync("branch-a")).Ok);

		var result = await host.InvokeClientCommandAsync(
			SessionCommands.UnloadSession,
			new { });

		Assert.True(result.Ok, result.Error);
		host.SelectWorkspaceSession();
		await Wait.ForAsync<bool>(() =>
			SessionById(host.Bridge, "branch-a").GetProperty("loaded").GetBoolean()
				? null
				: true);
		Assert.Null(host.Core.SessionForTest("branch-a"));
		Assert.False(SessionById(host.Bridge, "branch-a").GetProperty("loaded").GetBoolean());
		Assert.Empty(dispatcherErrors);
	}

	[Fact]
	public async Task UnknownSessionBaseIsRejectedInsteadOfImplicitlyUsingTheInvokingSession() {
		await using var host = await TestHost.StartAsync();

		var result = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "ambiguous-base",
			Base = "current",
		});

		Assert.False(result.Ok);
		Assert.Contains("Unknown session base", result.Error, StringComparison.Ordinal);
		Assert.Null(host.Core.SessionForTest("ambiguous-base"));
	}

	[Fact]
	public async Task CodexSession_RestoresAsCodexAfterRestart() {
		await using var host = await TestHost.StartAsync();
		var result = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "codex-branch",
			Base = "main",
			AgentProviderId = "codex",
		});
		Assert.True(result.Ok);

		await host.RestartAsync();

		var session = SessionById(host.Bridge, "codex-branch");
		Assert.Equal("codex", session.GetProperty("providerId").GetString());
		Assert.Equal("structured", session.GetProperty("agentSurface").GetString());
		Assert.Equal(2, session.GetProperty("agentInputProtocol").GetInt32());
		Assert.True(session.GetProperty("loaded").GetBoolean());
		Assert.False(session.TryGetProperty("active", out _));
	}

	[Fact]
	public async Task CodexWorktree_RestoresProvider_WhenSessionOverlayIsMissing() {
		await using var host = await TestHost.StartAsync();
		var result = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "codex-branch",
			Base = "main",
			AgentProviderId = "codex",
		});
		Assert.True(result.Ok);
		string overlay = WeaviePaths.WorkspaceSessionsFile(WorkspaceId.ForPath(host.RepoRoot));

		await host.RestartAsync(() => File.WriteAllText(overlay, """{"version":3,"sessions":[]}"""));

		var session = SessionById(host.Bridge, "codex-branch");
		Assert.Equal("codex", session.GetProperty("providerId").GetString());
		Assert.False(session.GetProperty("loaded").GetBoolean());
	}

	[Fact]
	public async Task DormantUnknownProvider_DoesNotHideWorkspaceSession() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("branch-a")).Ok);
		Assert.True((await host.UnloadSessionAsync("branch-a")).Ok);
		var registry = new WorktreeRegistry(
			new LocalFileSystem(),
			WeaviePaths.WorkspaceWorktreesFile(WorkspaceId.ForPath(host.RepoRoot)));
		var record = Assert.IsType<WorktreeRecord>(registry.FindByBranch("branch-a"));
		registry.Add(record with { AgentProviderId = "removed-provider" });

		await host.RestartAsync();

		Assert.Same(host.WorkspaceSession, host.SelectedSession);
		var stale = SessionById(host.Bridge, "branch-a");
		Assert.Equal("removed-provider", stale.GetProperty("providerId").GetString());
		Assert.Equal("unavailable", stale.GetProperty("agentSurface").GetString());
		Assert.False(stale.GetProperty("loaded").GetBoolean());
	}

	[Fact]
	public async Task StaleOverlayNamingAMissingSlot_IsSkipped() {
		await using var host = await TestHost.StartAsync();

		// An overlay naming a session whose worktree no longer reconciles (removed out-of-band). Restore must skip
		// it — the live git set wins over the stored overlay — and leave the workspace session active.
		string overlay = WeaviePaths.WorkspaceSessionsFile(WorkspaceId.ForPath(host.RepoRoot));
		Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
		File.WriteAllText(overlay,
			"""{"version":3,"sessions":[{"id":"ghost","label":"ghost","worktreePath":"/gone","managedCheckout":true,"loaded":true,"agentProviderId":"claude","editorSession":{"active":null,"open":[]}}]}""");

		await host.RestartAsync();

		Assert.Same(host.WorkspaceSession, host.SelectedSession);
		Assert.DoesNotContain(
			host.Bridge.LastEvent("sessions", "catalog")!.Value.EnumerateArray(),
			s => s.GetProperty("id").GetString() == "ghost");
	}

	[Fact]
	public async Task BranchList_IncludesSurfacedSessionBranches_ForSwitching() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("branch-a")).Ok);
		host.Bridge.Clear();

		string[] branches = await host.HostRequestAsync<string[]>("git", "branches", new { });

		Assert.Contains("branch-a", branches);
	}

	[Fact]
	public async Task FileOpenedWithNoPageIsPresentInTheReconnectSnapshot() {
		await using var host = await TestHost.StartAsync(_ => { }, sendReady: false);
		string path = Path.Combine(host.RepoRoot, "readme.txt");

		await host.WorkspaceSession.FileOpener.OpenAsync(
			path,
			line: 1,
			preview: false,
			scratch: false);
		host.Bridge.Clear();
		await host.ConnectAsync();

		var restore = host.Bridge.LastEvent(host.WorkspaceSession.Address, "editor", "restore");
		Assert.True(restore.HasValue);
		Assert.Contains(
			restore!.Value.GetProperty("session").GetProperty("open").EnumerateArray(),
			entry => entry.GetProperty("path").GetString() == path);
	}

	[Fact]
	public async Task EverySessionRestoresItsOwnEditorState() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("branch-a")).Ok);
		string workspaceFile = Path.Combine(host.RepoRoot, "readme.txt");
		var branch = host.Session("branch-a");
		string branchFile = Path.Combine(branch.WorkspaceRoot, "readme.txt");
		host.SelectWorkspaceSession();
		host.SessionEvent(host.WorkspaceSession, "editor", "sessionChanged", new {
			session = new { active = workspaceFile, open = new[] { new { path = workspaceFile } } },
		});
		host.SelectSession("branch-a");
		host.SessionEvent(branch, "editor", "sessionChanged", new {
			session = new { active = branchFile, open = new[] { new { path = branchFile } } },
		});

		await host.RestartAsync();

		Assert.Equal(workspaceFile, RestoredActive(host, host.WorkspaceSession));
		Assert.Equal(branchFile, RestoredActive(host, host.Session("branch-a")));
	}

	[Fact]
	public async Task DormantSessionEditorStateSurvivesConsecutiveRestarts() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("branch-a")).Ok);
		var branch = host.Session("branch-a");
		string branchFile = Path.Combine(branch.WorkspaceRoot, "readme.txt");
		host.SessionEvent(branch, "editor", "sessionChanged", new {
			session = new { active = branchFile, open = new[] { new { path = branchFile } } },
		});
		Assert.True((await host.UnloadSessionAsync("branch-a")).Ok);

		await host.RestartAsync();
		await host.RestartAsync();
		Assert.True((await host.InvokeClientCommandAsync(
			SessionCommands.LoadSession,
			new { id = "branch-a" })).Ok);

		Assert.Equal(branchFile, RestoredActive(host, host.Session("branch-a")));
	}

	[Fact]
	public async Task SessionSync_ReplaysOnlyToTheRequestingPage() {
		await using var host = await TestHost.StartAsync();
		var requester = new WebPeer("reconnecting-page");
		const string requestId = "reconnect-sync";
		host.WorkspaceSession.State.Set("syncProbe", "state", "snapshot", new { value = 7 });
		host.Bridge.Clear();

		host.Bridge.Receive(
			requester,
			MessageEnvelope.SessionRequest(
				host.WorkspaceSession.Address,
				requestId,
				"lifecycle",
				"sync",
				JsonSerializer.SerializeToElement(new { })).ToJson());

		await Wait.UntilAsync(() => host.Bridge.Sent.Any(message =>
			MessageEnvelope.TryParse(message.Json, out var envelope)
			&& envelope is {
				Kind: MessageKind.Event,
				Feature: "syncProbe",
				Name: "snapshot",
			}));

		var (replayPeer, _) = Assert.Single(host.Bridge.Sent, message =>
			MessageEnvelope.TryParse(message.Json, out var envelope)
			&& envelope is {
				Kind: MessageKind.Event,
				Feature: "syncProbe",
				Name: "snapshot",
			});
		Assert.Equal(requester, replayPeer);
		Assert.DoesNotContain(host.Bridge.Broadcasts, json =>
			MessageEnvelope.TryParse(json, out var envelope)
			&& envelope is {
				Kind: MessageKind.Event,
				Feature: "syncProbe",
				Name: "snapshot",
			});
	}

	private static void AssertAddress(CommandResult result, HostSession session) {
		Assert.True(result.Ok, result.Error);
		using var data = JsonDocument.Parse(Assert.IsType<string>(result.DataJson));
		var address = data.RootElement.GetProperty("address");
		Assert.Equal(session.SlotId, address.GetProperty("slot").GetString());
		Assert.Equal(session.Incarnation, address.GetProperty("incarnation").GetString());
		Assert.False(address.TryGetProperty("Slot", out _));
	}

	private static string? RestoredActive(TestHost host, HostSession session) =>
		host.Bridge.LastEvent(session.Address, "editor", "restore")?.GetProperty("session")
			.GetProperty("active").GetString();
}
