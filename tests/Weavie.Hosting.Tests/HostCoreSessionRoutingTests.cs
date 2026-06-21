using System.Text.Json;
using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// End-to-end session-routing tests against a real <see cref="HostCore"/> over a temp git repo (two live
/// sessions). These drive the same web messages the page sends and assert on what the host posts back —
/// proving the cross-session invariants: fs routes by path (not active session), the editor-session owner
/// guard rejects post-switch stragglers, and a switch re-points the LSP at the incoming worktree. Requires
/// <c>git</c> on PATH.
/// </summary>
[Collection("host-integration")]
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
	public async Task Switch_RepointsLspAtTheIncomingWorktree() {
		await using var host = await TestHost.StartAsync();

		var created = await host.CreateSessionAsync("feature");
		Assert.True(created.Ok, created.Error);

		var lsp = host.Bridge.LastOfType("lsp-config");
		Assert.True(lsp.HasValue);
		string? workspace = lsp!.Value.GetProperty("config").GetProperty("workspace").GetString();
		// The feature session's LSP bridge is rooted at its own worktree, not the primary checkout.
		Assert.NotNull(workspace);
		Assert.NotEqual(host.RepoRoot, workspace);
	}
}
