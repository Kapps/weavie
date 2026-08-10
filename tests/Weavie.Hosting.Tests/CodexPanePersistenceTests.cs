using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The native Codex pane's rendered output is durable: it survives a worker restart (the pane is a structured
/// stream with no self-repainting TUI, so without a persisted transcript a reopened session comes up blank), and
/// a fresh thread drops the stale transcript. Requires <c>git</c> on PATH.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class CodexPanePersistenceTests {
	private static async Task<TestHost> StartWithCodexSessionAsync(string branch) {
		var host = await TestHost.StartAsync();
		// Post each pane message immediately so a message is asserted the moment it's emitted (no coalesce window).
		host.Settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L));
		var result = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = branch,
			Base = "main",
			AgentProviderId = "codex",
		});
		Assert.True(result.Ok);
		return host;
	}

	private static bool HasPaneMessage(
		FakeHostBridge bridge,
		HostSession session,
		string type,
		string? text) {
		bool Matches(JsonElement message) =>
			message.GetProperty("type").GetString() == type
			&& (text is null || message.GetProperty("text").GetString() == text);

		foreach (var posted in bridge.PostedEvents(session.Address, "agent", "pane")) {
			if (Matches(posted)) {
				return true;
			}
		}

		foreach (var posted in bridge.PostedEvents(session.Address, "agent", "paneBatch")) {
			if (posted.GetProperty("messages").EnumerateArray().Any(Matches)) {
				return true;
			}
		}
		return false;
	}

	private static async Task<JsonElement[]> ReadHistoryAsync(TestHost host, HostSession session) {
		var fragments = new List<JsonElement>();
		JsonElement? cursor = null;
		do {
			var page = await host.SessionRequestAsync<JsonElement>(
				session,
				"agent",
				"historyPage",
				new { cursor });
			fragments.InsertRange(0, page.GetProperty("messages").EnumerateArray().Select(message => message.Clone()));
			var next = page.GetProperty("cursor");
			cursor = next.ValueKind == JsonValueKind.Null ? null : next.Clone();
		} while (cursor is not null);

		return [.. fragments
			.GroupBy(fragment => (
				fragment.GetProperty("generation").GetInt64(),
				fragment.GetProperty("ordinal").GetInt64(),
				fragment.GetProperty("revision").GetInt64()))
			.Select(group => JsonDocument.Parse(
				string.Concat(group.Select(fragment => fragment.GetProperty("json").GetString())))
				.RootElement.Clone())];
	}

	private static bool Contains(JsonElement[] messages, string type, string text) =>
		messages.Any(message =>
			message.GetProperty("type").GetString() == type
			&& message.GetProperty("text").GetString() == text);

	private static string[] TranscriptFiles(TestHost host) {
		string dir = WeaviePaths.WorkspaceAgentPanesDir(WorkspaceId.ForPath(host.RepoRoot));
		return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : [];
	}

	[Fact]
	public async Task CodexPaneTranscript_SurvivesWorkerRestart() {
		await using var host = await StartWithCodexSessionAsync("codex-branch");
		var session = host.Session("codex-branch");
		host.SessionEvent(
			session,
			"agent",
			"submit",
			new { id = "", prompt = "hello", attachmentIds = Array.Empty<string>(), skills = Array.Empty<string>() });
		Assert.True(HasPaneMessage(host.Bridge, session, "item-completed", "echo: hello"));

		await host.RestartAsync();
		session = host.Session("codex-branch");
		var history = await ReadHistoryAsync(host, session);

		Assert.True(Contains(history, "user-message", "hello"));
		Assert.True(Contains(history, "item-completed", "echo: hello"));
	}

	[Fact]
	public async Task CodexPaneTranscript_ReturnsWhenDormantSessionLoadsOnAConnectedPage() {
		await using var host = await StartWithCodexSessionAsync("codex-branch");
		var session = host.Session("codex-branch");
		host.SessionEvent(
			session,
			"agent",
			"submit",
			new { id = "", prompt = "hello", attachmentIds = Array.Empty<string>(), skills = Array.Empty<string>() });
		Assert.True(HasPaneMessage(host.Bridge, session, "item-completed", "echo: hello"));
		Assert.True((await host.UnloadSessionAsync("codex-branch")).Ok);
		host.Bridge.Clear();

		var loaded = await host.InvokeClientCommandAsync(
			SessionCommands.LoadSession,
			new { id = "codex-branch" });
		Assert.True(loaded.Ok, loaded.Error);
		session = host.Session("codex-branch");
		var history = await ReadHistoryAsync(host, session);
		Assert.True(Contains(history, "item-completed", "echo: hello"));
	}

	[Fact]
	public async Task LifecycleSync_DoesNotPushTranscriptHistory() {
		await using var host = await StartWithCodexSessionAsync("codex-branch");
		var session = host.Session("codex-branch");
		host.SessionEvent(
			session,
			"agent",
			"submit",
			new { id = "", prompt = "hello", attachmentIds = Array.Empty<string>(), skills = Array.Empty<string>() });

		host.Bridge.Clear();
		await host.SessionRequestAsync<JsonElement>(session, "lifecycle", "sync", new { });
		Assert.Null(host.Bridge.LastEvent(session.Address, "agent", "paneReset"));
		Assert.Null(host.Bridge.LastEvent(session.Address, "agent", "paneBatch"));
		Assert.True(Contains(await ReadHistoryAsync(host, session), "item-completed", "echo: hello"));
	}

	[Fact]
	public async Task BackgroundSession_PublishesItsOwnPaneWithoutBeingSelected() {
		await using var host = await StartWithCodexSessionAsync("codex-branch");
		var background = host.Session("codex-branch");
		host.SelectWorkspaceSession();
		host.Bridge.Clear();

		host.SessionEvent(
			background,
			"agent",
			"submit",
			new { id = "", prompt = "hello", attachmentIds = Array.Empty<string>(), skills = Array.Empty<string>() });

		Assert.Same(host.WorkspaceSession, host.SelectedSession);
		Assert.True(HasPaneMessage(host.Bridge, background, "user-message", "hello"));
		Assert.True(HasPaneMessage(host.Bridge, background, "item-completed", "echo: hello"));
		Assert.Empty(host.Bridge.PostedEvents(host.WorkspaceSession.Address, "agent", "pane"));
	}

	[Fact]
	public async Task ThreadReset_ClearsTranscript_SoRestartComesUpEmpty() {
		await using var host = await StartWithCodexSessionAsync("codex-branch");
		var session = host.Session("codex-branch");
		host.SessionEvent(
			session,
			"agent",
			"submit",
			new { id = "", prompt = "hello", attachmentIds = Array.Empty<string>(), skills = Array.Empty<string>() });
		Assert.Single(TranscriptFiles(host));

		host.SessionEvent(
			session,
			"agent",
			"submit",
			new {
				id = "",
				prompt = FakeCodexAgentProvider.ResetPrompt,
				attachmentIds = Array.Empty<string>(),
				skills = Array.Empty<string>(),
			});

		Assert.NotNull(host.Bridge.LastEvent(session.Address, "agent", "paneReset"));
		Assert.Empty(TranscriptFiles(host)); // the stale transcript file is removed

		await host.RestartAsync();
		session = host.Session("codex-branch");

		Assert.False(Contains(await ReadHistoryAsync(host, session), "item-completed", "echo: hello"));
	}
}
