using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The native structured pane's rendered output is durable: it survives a worker restart (the pane is a structured
/// stream with no self-repainting TUI, so without a persisted transcript a reopened session comes up blank), and
/// a fresh thread drops the stale transcript. Requires <c>git</c> on PATH.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class StructuredPanePersistenceTests {
	private static async Task<TestHost> StartWithStructuredSessionAsync(string branch) {
		var host = await TestHost.StartAsync();
		// Post each pane message immediately so a message is asserted the moment it's emitted (no coalesce window).
		host.Settings.Set(AgentSettings.PaneCoalesceMs, JsonSerializer.SerializeToElement(0L));
		var result = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = branch,
			Base = "main",
			AgentProviderId = "structured",
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
					new { cursor, knownGeneration = (long?)null, knownRevision = (long?)null });
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

	private static void Submit(TestHost host, HostSession session, string prompt) =>
		host.SessionEvent(
			session,
			"agent",
			"submit",
			new { id = "", prompt, kind = "prompt", commandName = "", attachmentIds = Array.Empty<string>() });

	// Weavie keeps no transcript cache: a provider that cannot replay its own conversation comes back empty
	// rather than being handed a stale copy that looks live.
	[Fact]
	public async Task WithoutProviderReplay_RestartComesUpEmpty() {
		await using var host = await StartWithStructuredSessionAsync("structured-branch");
		var session = host.Session("structured-branch");
		Submit(host, session, "hello");
		Assert.True(HasPaneMessage(host.Bridge, session, "item-completed", "echo: hello"));

		await host.RestartAsync(FakeStructuredAgentProvider.ForgetTranscripts);
		session = host.Session("structured-branch");

		Assert.False(Contains(await ReadHistoryAsync(host, session), "item-completed", "echo: hello"));
	}

	// The provider is now the only source of the transcript, so a replayed conversation has to arrive whole:
	// every turn the user had before the restart must be readable after it, not just the leading few.
	[Fact]
	public async Task ProviderReplay_RestoresEveryTurn() {
		await using var host = await StartWithStructuredSessionAsync("structured-branch");
		var session = host.Session("structured-branch");
		const int turns = 25;
		for (int turn = 1; turn <= turns; turn++) {
			Submit(host, session, $"hello {turn}");
		}

		await host.RestartAsync();
		session = host.Session("structured-branch");

		var history = await ReadHistoryAsync(host, session);
		int[] missing = [.. Enumerable.Range(1, turns)
			.Where(turn => !Contains(history, "item-completed", $"echo: hello {turn}"))];
		Assert.Empty(missing);
	}

	// A provider emits its own chrome (thread-ready) before it replays, so a restore always lands over a
	// non-empty pane and does void the ordinals a client holds. paneReset says so, but it is a broadcast a page
	// reconnecting mid-load can miss, and a bare reset leaves nothing else to notice. The restored records must
	// therefore be readable straight off the live stream, without the client ever asking for history.
	[Fact]
	public async Task ProviderReplay_RepublishesRestoredRecordsLive() {
		await using var host = await StartWithStructuredSessionAsync("structured-branch");
		var session = host.Session("structured-branch");
		Submit(host, session, "hello");

		await host.RestartAsync();
		session = host.Session("structured-branch");

		Assert.True(HasPaneMessage(host.Bridge, session, "item-completed", "echo: hello"));
	}

	[Fact]
	public async Task LifecycleSync_DoesNotPushTranscriptHistory() {
		await using var host = await StartWithStructuredSessionAsync("structured-branch");
		var session = host.Session("structured-branch");
		host.SessionEvent(
			session,
			"agent",
			"submit",
			new { id = "", prompt = "hello", kind = "prompt", commandName = "", attachmentIds = Array.Empty<string>() });

		host.Bridge.Clear();
		await host.SessionRequestAsync<JsonElement>(session, "lifecycle", "sync", new { });
		Assert.Null(host.Bridge.LastEvent(session.Address, "agent", "paneReset"));
		Assert.Null(host.Bridge.LastEvent(session.Address, "agent", "paneBatch"));
		Assert.True(Contains(await ReadHistoryAsync(host, session), "item-completed", "echo: hello"));
	}

	[Fact]
	public async Task BackgroundSession_PublishesItsOwnPaneWithoutBeingSelected() {
		await using var host = await StartWithStructuredSessionAsync("structured-branch");
		var background = host.Session("structured-branch");
		host.SelectWorkspaceSession();
		host.Bridge.Clear();

		host.SessionEvent(
			background,
			"agent",
			"submit",
			new { id = "", prompt = "hello", kind = "prompt", commandName = "", attachmentIds = Array.Empty<string>() });

		Assert.Same(host.WorkspaceSession, host.SelectedSession);
		Assert.True(HasPaneMessage(host.Bridge, background, "user-message", "hello"));
		Assert.True(HasPaneMessage(host.Bridge, background, "item-completed", "echo: hello"));
		Assert.Empty(host.Bridge.PostedEvents(host.WorkspaceSession.Address, "agent", "pane"));
	}

	[Fact]
	public async Task ThreadReset_ClearsTranscript_SoRestartComesUpEmpty() {
		await using var host = await StartWithStructuredSessionAsync("structured-branch");
		var session = host.Session("structured-branch");
		host.SessionEvent(
			session,
			"agent",
			"submit",
			new { id = "", prompt = "hello", kind = "prompt", commandName = "", attachmentIds = Array.Empty<string>() });
		host.SessionEvent(
			session,
			"agent",
			"submit",
			new {
				id = "",
				prompt = FakeStructuredAgentProvider.ResetPrompt,
				kind = "prompt",
				commandName = "",
				attachmentIds = Array.Empty<string>(),
			});

		Assert.NotNull(host.Bridge.LastEvent(session.Address, "agent", "paneReset"));

		await host.RestartAsync();
		session = host.Session("structured-branch");

		Assert.False(Contains(await ReadHistoryAsync(host, session), "item-completed", "echo: hello"));
	}
}
