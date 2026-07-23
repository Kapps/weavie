using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Diffs;
using Weavie.Core.Hooks;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// End-to-end session-routing tests against a real <see cref="HostCore"/> over a temp git repo (two live
/// sessions). These drive the same web messages the page sends and assert on what the host posts back —
/// proving the cross-session invariants: fs routes by path (not active session), the editor-session owner
/// guard rejects post-switch stragglers, and a switch re-points the LSP at the incoming worktree. Requires
/// <c>git</c> on PATH.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSessionRoutingTests {
	private static string Msg(object value) => JsonSerializer.Serialize(value);

	private static JsonElement? ReplyById(FakeHostBridge bridge, string type, string id) {
		foreach (var element in bridge.PostedOfType(type)) {
			if (element.TryGetProperty("id", out var idEl) && idEl.GetString() == id) {
				return element;
			}
		}

		return null;
	}

	[Fact]
	public async Task Ready_SeedsEditorSessionStampedWithThePrimaryOwner() {
		await using var host = await TestHost.StartAsync();

		var seed = host.Bridge.LastOfType("set-editor-session");
		Assert.True(seed.HasValue);
		Assert.False(string.IsNullOrEmpty(seed!.Value.GetProperty("sessionId").GetString()));
	}

	[Fact]
	public async Task Projection_CarriesTheRailSlotIdentitySeparatelyFromItsInternalSessionOwner() {
		await using var host = await TestHost.StartAsync();

		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		var projection = host.Bridge.LastOfType("set-editor-session")!.Value;

		Assert.Equal("feature", projection.GetProperty("railSessionId").GetString());
		Assert.NotEqual("feature", projection.GetProperty("sessionId").GetString());
	}

	[Fact]
	public async Task Reconnect_ResyncsLspConfigForTheActiveSession() {
		await using var host = await TestHost.StartAsync();

		// A bridge reconnect replays `ready`. The host must re-advertise the active session's LSP catalog so the
		// page rebinds language clients on fresh channels — the resync the remote path needs (a network drop, a
		// worker restart). See docs/specs/lsp-over-bridge.md.
		host.Bridge.Clear();
		host.Send("""{"type":"ready"}""");

		var lsp = host.Bridge.LastOfType("lsp-config");
		Assert.True(lsp.HasValue);
		Assert.False(string.IsNullOrEmpty(lsp!.Value.GetProperty("config").GetProperty("slot").GetString()));
	}

	[Fact]
	public async Task Ready_EndsItsSynchronousReplayWithBridgeReady() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Clear();
		// Async enrichments may follow; the bridge marker is the tail of the synchronous replay contract.
		int receiveThread = Environment.CurrentManagedThreadId;
		var synchronousReplay = new List<string>();
		void CaptureSynchronousPost(string json) {
			if (Environment.CurrentManagedThreadId == receiveThread) {
				synchronousReplay.Add(json);
			}
		}

		host.Bridge.MessagePosted += CaptureSynchronousPost;
		try {
			host.Bridge.Receive($$"""{"type":"ready","bridgeId":"page-1","pageId":"{{TestHost.TestPageId}}"}""");
		} finally {
			host.Bridge.MessagePosted -= CaptureSynchronousPost;
		}

		var info = host.Bridge.LastOfType("host-info");
		Assert.True(info.HasValue);
		Assert.Equal(1, info.Value.GetProperty("readyReplayProtocol").GetInt32());
		using var tail = JsonDocument.Parse(synchronousReplay[^1]);
		Assert.Equal("bridge-ready", tail.RootElement.GetProperty("type").GetString());
		Assert.Equal("page-1", tail.RootElement.GetProperty("bridgeId").GetString());
	}

	[Fact]
	public async Task Ready_FromAnotherPage_DoesNotStealTheMountedEditorProjection() {
		await using var host = await TestHost.StartAsync();
		string primaryId = host.PrimaryId;
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		host.Bridge.Clear();

		// Ambient state replay is connection-wide, but editor ownership is acquired separately. A second tab or a
		// background transport reconnect must not bump the revision and mute the page that already mounted it.
		host.Bridge.Receive("""{"type":"ready","pageId":"another-page"}""");
		Assert.Empty(host.Bridge.PostedOfType("set-editor-session"));

		host.Send(Msg(new {
			type = "open-editors-changed",
			sessionId = primaryId,
			editors = new[] { new { path, isActive = true, isPinned = false, isPreview = false } },
		}));
		Assert.Equal(path, Assert.Single(host.Core.ActiveSessionForTest()!.Editor.OpenEditors).FilePath);
	}

	[Fact]
	public async Task CurrentProjectionMessages_AreAcceptedBeforeMount() {
		await using var host = await TestHost.StartAsync();
		host.AutoMountEditorProjection = false;
		await host.CreateSessionAsync("feature");
		var session = host.Core.ActiveSessionForTest()!;
		string path = Path.Combine(session.WorkspaceRoot, "readme.txt");

		// The tab store reports the newly committed set synchronously, before Monaco's asynchronous mount ack.
		host.Send(Msg(new {
			type = "open-editors-changed",
			sessionId = session.Id,
			editors = new[] { new { path, isActive = true, isPinned = false, isPreview = false } },
		}));

		Assert.Equal(path, Assert.Single(session.Editor.OpenEditors).FilePath);
		host.MountEditorProjection();
	}

	[Fact]
	public async Task LateMountFromAbaMiddleProjection_CannotOwnTheReturnedSession() {
		await using var host = await TestHost.StartAsync();
		string primaryId = host.PrimaryId;
		host.AutoMountEditorProjection = false;
		await host.CreateSessionAsync("feature");
		var middle = host.Bridge.LastOfType("set-editor-session")!.Value;

		host.Send(Msg(new { type = "switch-session", id = primaryId }));
		var returned = host.Bridge.LastOfType("set-editor-session")!.Value;
		Assert.True(returned.GetProperty("projectionRevision").GetInt64() > middle.GetProperty("projectionRevision").GetInt64());

		// B's delayed ack arrives after A₂ was offered. Session id alone would accept this A→B→A class of
		// race; the exact host epoch/revision/page fence must reject the middle projection.
		host.Bridge.Receive(Msg(new {
			type = "editor-projection-mounted",
			sessionId = middle.GetProperty("sessionId").GetString(),
			projectionEpoch = middle.GetProperty("projectionEpoch").GetString(),
			projectionRevision = middle.GetProperty("projectionRevision").GetInt64(),
			projectionPageId = middle.GetProperty("projectionPageId").GetString(),
		}));

		host.MountEditorProjection();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		host.Send(Msg(new {
			type = "open-editors-changed",
			sessionId = primaryId,
			editors = new[] { new { path, isActive = true, isPinned = false, isPreview = false } },
		}));
		Assert.Equal(path, Assert.Single(host.Core.ActiveSessionForTest()!.Editor.OpenEditors).FilePath);
	}

	[Fact]
	public async Task MountFromAnotherPage_CannotActivateTheOfferedProjection() {
		await using var host = await TestHost.StartAsync();
		host.AutoMountEditorProjection = false;
		host.Send(Msg(new { type = "acquire-editor", pageId = TestHost.TestPageId }));
		var projection = host.Bridge.LastOfType("set-editor-session")!.Value;
		var session = host.Core.ActiveSessionForTest()!;
		session.EditorChannel.ShowDiff("diff-page", """{"type":"show-diff","id":"diff-page"}""");

		host.Bridge.Receive(Msg(new {
			type = "editor-projection-mounted",
			sessionId = projection.GetProperty("sessionId").GetString(),
			projectionEpoch = projection.GetProperty("projectionEpoch").GetString(),
			projectionRevision = projection.GetProperty("projectionRevision").GetInt64(),
			projectionPageId = "another-page",
		}));

		Assert.Empty(host.Bridge.PostedOfType("show-diff"));
		host.MountEditorProjection();
		Assert.Single(host.Bridge.PostedOfType("show-diff"));
	}

	[Fact]
	public async Task RepeatedMount_ReplaysTheMountedEditorsDurableDiff() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		session.EditorChannel.ShowDiff("remount", """{"type":"show-diff","id":"remount"}""");
		Assert.Single(host.Bridge.PostedOfType("show-diff"));
		host.Bridge.Clear();

		host.MountEditorProjection();

		Assert.Single(host.Bridge.PostedOfType("show-diff"));
	}

	[Fact]
	public async Task PageRelease_UnbindsItsSupersedingOfferWithoutLeavingAGhostOwner() {
		await using var host = await TestHost.StartAsync();
		host.AutoMountEditorProjection = false;
		host.Send(Msg(new { type = "acquire-editor", pageId = TestHost.TestPageId }));
		var oldOffer = host.Bridge.LastOfType("set-editor-session")!.Value;
		host.Send(Msg(new { type = "acquire-editor", pageId = TestHost.TestPageId }));
		var newOffer = host.Bridge.LastOfType("set-editor-session")!.Value;
		Assert.True(newOffer.GetProperty("projectionRevision").GetInt64()
			> oldOffer.GetProperty("projectionRevision").GetInt64());

		host.Bridge.Receive(Msg(new {
			type = "release-editor",
			sessionId = oldOffer.GetProperty("sessionId").GetString(),
			projectionEpoch = oldOffer.GetProperty("projectionEpoch").GetString(),
			projectionRevision = oldOffer.GetProperty("projectionRevision").GetInt64(),
			projectionPageId = oldOffer.GetProperty("projectionPageId").GetString(),
		}));
		host.Bridge.Clear();

		var created = await host.Core.NewSessionAsync(
			new NewSessionRequest { Branch = "released", Base = "main" }, CancellationToken.None);
		Assert.True(created.Ok, created.Error);
		Assert.Empty(host.Bridge.PostedOfType("set-editor-session"));

		host.Send(Msg(new { type = "acquire-editor", pageId = TestHost.TestPageId }));
		Assert.Single(host.Bridge.PostedOfType("set-editor-session"));
	}

	[Fact]
	public async Task DisconnectedPage_CannotRemainTheEditorOwner() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.DisconnectPage(TestHost.TestPageId);
		host.Bridge.Clear();

		var created = await host.Core.NewSessionAsync(
			new NewSessionRequest { Branch = "disconnected", Base = "main" }, CancellationToken.None);
		Assert.True(created.Ok, created.Error);
		Assert.Empty(host.Bridge.PostedOfType("set-editor-session"));
	}

	[Fact]
	public async Task ProjectionFromThePriorHostEpoch_CannotMutateTheRestartedSession() {
		await using var host = await TestHost.StartAsync();
		var oldProjection = host.Bridge.LastOfType("set-editor-session")!.Value.Clone();
		await host.RestartAsync();
		var currentProjection = host.Bridge.LastOfType("set-editor-session")!.Value;
		Assert.NotEqual(
			oldProjection.GetProperty("projectionEpoch").GetString(),
			currentProjection.GetProperty("projectionEpoch").GetString());

		string path = Path.Combine(host.RepoRoot, "readme.txt");
		host.Bridge.Receive(Msg(new {
			type = "editor-session-changed",
			sessionId = oldProjection.GetProperty("sessionId").GetString(),
			projectionEpoch = oldProjection.GetProperty("projectionEpoch").GetString(),
			projectionRevision = oldProjection.GetProperty("projectionRevision").GetInt64(),
			projectionPageId = oldProjection.GetProperty("projectionPageId").GetString(),
			session = new { active = path, open = new[] { new { path } } },
		}));

		Assert.Empty(host.Core.ActiveSessionForTest()!.Editor.OpenEditors);
	}

	[Fact]
	public async Task FileEvents_DuringProjectionRebind_AreDeferredUntilMount() {
		await using var host = await TestHost.StartAsync();
		host.AutoMountEditorProjection = false;
		await host.CreateSessionAsync("feature");
		var session = host.Core.ActiveSessionForTest()!;
		string path = Path.Combine(session.WorkspaceRoot, "readme.txt");
		var mutation = new AgentMutation.File(path, Cwd: null, ProvidesEditLocation: true);

		host.Bridge.Clear();
		session.Events.Observe(new AgentToolStarting(mutation));
		File.WriteAllText(path, "hello\nchanged while mounting\n");
		session.Events.Observe(new AgentToolCompleted(mutation));

		Assert.Empty(host.Bridge.PostedOfType("fs-change"));
		Assert.Empty(host.Bridge.PostedOfType("turn-diff"));
		host.MountEditorProjection();
		Assert.NotEmpty(host.Bridge.PostedOfType("fs-change"));
		Assert.NotEmpty(host.Bridge.PostedOfType("turn-diff"));
	}

	[Fact]
	public async Task LegacyPage_MountsAtMonacoReadyAndCanReportItsEditorState() {
		await using var host = await TestHost.StartAsync(_ => { }, sendReady: false);
		var session = host.Core.ActiveSessionForTest()!;
		session.EditorChannel.ShowDiff("legacy", """{"type":"show-diff","id":"legacy"}""");
		host.Bridge.Receive("""{"type":"ready"}""");
		var seed = host.Bridge.LastOfType("set-editor-session");
		Assert.True(seed.HasValue);
		Assert.Equal(JsonValueKind.Null, seed.Value.GetProperty("projectionRevision").ValueKind);
		Assert.Empty(host.Bridge.PostedOfType("show-diff"));
		string changed = Path.Combine(host.RepoRoot, "readme.txt");
		var mutation = new AgentMutation.File(changed, Cwd: null, ProvidesEditLocation: true);
		session.Events.Observe(new AgentToolStarting(mutation));
		File.WriteAllText(changed, "changed before Monaco\n");
		session.Events.Observe(new AgentToolCompleted(mutation));
		Assert.Empty(host.Bridge.PostedOfType("turn-diff"));

		string path = changed;
		host.Bridge.Receive(Msg(new {
			type = "open-editors-changed",
			sessionId = seed.Value.GetProperty("sessionId").GetString(),
			editors = new[] { new { path, isActive = true, isPinned = false, isPreview = false } },
		}));

		Assert.Equal(path, Assert.Single(host.Core.ActiveSessionForTest()!.Editor.OpenEditors).FilePath);
		host.Bridge.Receive("""{"type":"monaco-ready"}""");
		Assert.Single(host.Bridge.PostedOfType("show-diff"));
		Assert.NotEmpty(host.Bridge.PostedOfType("turn-diff"));
	}

	[Fact]
	public async Task LegacySessionSwitch_ClearsTheOutgoingReviewProjection() {
		await using var host = await TestHost.StartAsync(_ => { }, sendReady: false);
		host.Bridge.Receive("""{"type":"ready"}""");
		host.Bridge.Receive("""{"type":"monaco-ready"}""");
		host.Bridge.Clear();

		var created = await host.Core.NewSessionAsync(
			new NewSessionRequest { Branch = "legacy-switch", Base = "main" }, CancellationToken.None);

		Assert.True(created.Ok, created.Error);
		Assert.Single(host.Bridge.PostedOfType("turn-reset"));
	}

	[Fact]
	public async Task TerminalMessage_NamingAnUnknownSlot_IsIgnoredInsteadOfReachingTheActiveSession() {
		await using var host = await TestHost.StartAsync();
		int terminalsBefore = host.Platform.NoopLauncher.Created.Count;

		host.Send("""{"type":"term-ready","slot":"another-backend-session","session":"shell","cols":80,"rows":24}""");

		Assert.Equal(terminalsBefore, host.Platform.NoopLauncher.Created.Count);
		host.Send("""{"type":"term-ready","slot":"","session":"shell","cols":80,"rows":24}""");
		host.Send("""{"type":"term-ready","slot":null,"session":"shell","cols":80,"rows":24}""");
		Assert.Equal(terminalsBefore, host.Platform.NoopLauncher.Created.Count);
		// Slot-less messages remain compatible with legacy clients and still address the active session.
		host.Send("""{"type":"term-ready","session":"shell","cols":80,"rows":24}""");
		Assert.Equal(terminalsBefore + 1, host.Platform.NoopLauncher.Created.Count);
	}

	[Fact]
	public async Task SwitchSession_ReplaysStructuredControlsForTheActivatedSession() {
		await using var host = await TestHost.StartAsync();
		string primaryId = host.PrimaryId;
		var created = await host.Core.NewSessionAsync(new NewSessionRequest {
			Branch = "codex-controls",
			Base = "main",
			AgentProviderId = "codex",
		}, CancellationToken.None);
		Assert.True(created.Ok, created.Error);

		host.Send(Msg(new { type = "switch-session", id = primaryId }));
		host.Bridge.Clear();
		host.Send(Msg(new { type = "switch-session", id = "codex-controls" }));

		var controls = Assert.Single(host.Bridge.PostedOfType("agent-controls"));
		Assert.Equal("codex-controls", controls.GetProperty("slot").GetString());
		var state = controls.GetProperty("state");
		Assert.Equal("gpt-test", state.GetProperty("modelControl").GetProperty("value").GetString());
		Assert.Equal(
			["approvalPolicy", "sandbox"],
			state.GetProperty("axes").EnumerateArray().Select(axis => axis.GetProperty("id").GetString()));
	}

	[Fact]
	public async Task FsRead_RoutesByPathToTheOwningSession_EvenWhenItIsBackground() {
		await using var host = await TestHost.StartAsync();
		// A distinct on-disk marker in the PRIMARY worktree; not present in the feature worktree.
		string marker = Path.Combine(host.RepoRoot, "marker.txt");
		File.WriteAllText(marker, "PRIMARY-MARKER");

		// Switch focus to a new feature session, so the primary is now a BACKGROUND session.
		var created = await host.CreateSessionAsync("feature");
		Assert.True(created.Ok, created.Error);

		host.Send(Msg(new { type = "fs-read", id = "r1", path = marker }));

		var reply = ReplyById(host.Bridge, "fs-read-result", "r1");
		Assert.True(reply.HasValue);
		Assert.True(reply!.Value.GetProperty("ok").GetBoolean()); // routed to primary's provider, not the active feature's
		Assert.Equal("PRIMARY-MARKER", reply.Value.GetProperty("content").GetString());
	}

	[Fact]
	public async Task FsRead_PathOutsideEverySession_IsRefusedAsNotFound() {
		await using var host = await TestHost.StartAsync();
		string outside = Path.Combine(Path.GetTempPath(), "weavie-not-in-any-worktree-" + Guid.NewGuid().ToString("n") + ".txt");
		File.WriteAllText(outside, "nope");

		host.Send(Msg(new { type = "fs-read", id = "r2", path = outside }));

		var reply = ReplyById(host.Bridge, "fs-read-result", "r2");
		Assert.True(reply.HasValue);
		Assert.False(reply!.Value.GetProperty("ok").GetBoolean());
		Assert.Equal("FileNotFound", reply.Value.GetProperty("code").GetString());
	}

	[Fact]
	public async Task EditorSessionChanged_WithMatchingOwner_IsRecordedAndSurvivesASwitchRoundTrip() {
		await using var host = await TestHost.StartAsync();
		string primaryId = host.PrimaryId;
		string readme = Path.Combine(host.RepoRoot, "readme.txt"); // exists in the repo

		// The page reports the primary's tab set, correctly stamped with the primary session id.
		host.Send(Msg(new {
			type = "editor-session-changed",
			sessionId = primaryId,
			session = new { active = readme, open = new[] { new { path = readme } } },
		}));

		await host.CreateSessionAsync("feature"); // switch away to a feature session
		host.Bridge.Clear();
		host.Send(Msg(new { type = "switch-session", id = primaryId })); // switch back

		var push = host.Bridge.LastOfType("set-editor-session");
		Assert.True(push.HasValue);
		var open = push!.Value.GetProperty("session").GetProperty("open");
		Assert.Equal(1, open.GetArrayLength()); // the recorded tab came back — it wasn't lost
		Assert.Equal(readme, open[0].GetProperty("path").GetString());
	}

	[Fact]
	public async Task EditorSessionChanged_WithStaleOwner_IsRejected() {
		await using var host = await TestHost.StartAsync();
		string primaryId = host.PrimaryId;
		string readme = Path.Combine(host.RepoRoot, "readme.txt");

		// A straggler stamped with the WRONG session id (a different session) must not contaminate the active one.
		host.Send(Msg(new {
			type = "editor-session-changed",
			sessionId = "some-other-session",
			session = new { active = readme, open = new[] { new { path = readme } } },
		}));

		await host.CreateSessionAsync("feature");
		host.Bridge.Clear();
		host.Send(Msg(new { type = "switch-session", id = primaryId }));

		var push = host.Bridge.LastOfType("set-editor-session");
		Assert.True(push.HasValue);
		// The rejected message never reached the primary's EditorSession, so it's still empty.
		Assert.Equal(0, push!.Value.GetProperty("session").GetProperty("open").GetArrayLength());
	}

	[Fact]
	public async Task DefaultModeEdit_SurfacesInTheTurnReview() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest();
		Assert.NotNull(session);
		// A fresh session is in Claude's default edit mode — no hook has reported acceptEdits/bypass. This is the
		// case that used to be gated out of the editor review (the bug): the turn push was suppressed unless
		// AutoAppliesEdits.
		Assert.False(session!.ObservedMode.AutoAppliesEdits);

		string file = Path.Combine(host.RepoRoot, "readme.txt"); // exists in the repo ("hello\n")
		string input = Msg(new { file_path = file });

		// Replay the hook stream of a single Edit: baseline before, the edit lands on disk, post-tool records it —
		// exactly what the change tracker sees in production (minus the relay/pipe transport).
		session.Changes.Observe(new HookRequest { Event = HookEventKind.PreToolUse, ToolName = "Edit", ToolInputJson = input });
		File.WriteAllText(file, "hello\nworld\n");
		host.Bridge.Clear();
		session.Changes.Observe(new HookRequest { Event = HookEventKind.PostToolUse, ToolName = "Edit", ToolInputJson = input });

		// With the mode gate removed, a default-mode edit surfaces as a turn-changes review set naming the file —
		// the change tracker, not openDiff, is the review surface in every mode.
		var turn = host.Bridge.LastOfType("turn-changes");
		Assert.True(turn.HasValue, "a default-mode edit should push a turn-changes review set");
		var files = turn!.Value.GetProperty("files");
		Assert.Equal(1, files.GetArrayLength());
		Assert.Equal(file, files[0].GetProperty("path").GetString());
	}

	[Fact]
	public async Task BinaryFileMutation_RefreshesFileWithoutReviewPayloadOrReplay() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest();
		Assert.NotNull(session);
		string file = Path.Combine(host.RepoRoot, "archive.bin");
		var mutation = new AgentMutation.File(file, Cwd: null, ProvidesEditLocation: true);

		session!.Events.Observe(new AgentToolStarting(mutation));
		File.WriteAllBytes(file, [0x50, 0x4b, 0x00, 0xff]);
		host.Bridge.Clear();
		session.Events.Observe(new AgentToolCompleted(mutation));

		Assert.Single(host.Bridge.PostedOfType("fs-change"));
		Assert.Empty(host.Bridge.PostedOfType("turn-diff"));
		Assert.Empty(host.Bridge.PostedOfType("turn-changes"));

		host.Bridge.Clear();
		host.Send("""{"type":"monaco-ready"}""");
		Assert.Empty(host.Bridge.PostedOfType("turn-diff"));
		Assert.Empty(host.Bridge.PostedOfType("turn-changes"));
	}

	[Fact]
	public async Task SwitchBackToBackgroundSession_ReplaysHeldDiffOnlyAfterProjectionMount() {
		await using var host = await TestHost.StartAsync();
		var primary = host.Core.ActiveSessionForTest();
		Assert.NotNull(primary);
		string primaryId = host.PrimaryId;
		string file = Path.Combine(host.RepoRoot, "readme.txt"); // exists in the repo

		// Switch focus to a feature session, parking the primary as a BACKGROUND session.
		var created = await host.CreateSessionAsync("feature");
		Assert.True(created.Ok, created.Error);

		// The (now background) primary's Claude opens a blocking openDiff (default mode): the muted editor channel
		// HOLDS it, posting nothing into the foreground feature session.
		host.Bridge.Clear();
		_ = primary!.DiffPresenter.PresentDiffAsync(new DiffProposal(file, file, "hello\nworld\n", "readme.txt"), CancellationToken.None);
		Assert.Empty(host.Bridge.PostedOfType("show-diff")); // held while background

		// Switch back to the primary: the held diff cannot replay into the page until its exact returned projection
		// mounts. This is also the durability boundary for work that completed while the session was backgrounded.
		host.Bridge.Clear();
		host.AutoMountEditorProjection = false;
		host.Send(Msg(new { type = "switch-session", id = primaryId }));
		Assert.Empty(host.Bridge.PostedOfType("show-diff"));

		host.MountEditorProjection();
		Assert.Single(host.Bridge.PostedOfType("show-diff"));
	}

	[Fact]
	public async Task Switch_RepointsLspAtTheIncomingWorktree() {
		await using var host = await TestHost.StartAsync();

		var created = await host.CreateSessionAsync("feature");
		Assert.True(created.Ok, created.Error);

		var lsp = host.Bridge.LastOfType("lsp-config");
		Assert.True(lsp.HasValue);
		var config = lsp!.Value.GetProperty("config");
		string? workspace = config.GetProperty("workspace").GetString();
		// The feature session's LSP is rooted at its own worktree, not the primary checkout.
		Assert.NotNull(workspace);
		Assert.NotEqual(host.RepoRoot, workspace);
		// LSP rides the bridge now: the config carries the session slot to tag frames with, and no longer a
		// loopback URL or token (the old per-session socket is gone).
		Assert.False(string.IsNullOrEmpty(config.GetProperty("slot").GetString()));
		Assert.False(config.TryGetProperty("url", out _));
		Assert.False(config.TryGetProperty("token", out _));
	}
}
