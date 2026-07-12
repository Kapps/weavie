using Weavie.Core;
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
		var result = await host.Core.NewSessionAsync(new NewSessionRequest {
			Branch = branch,
			Base = "main",
			AgentProviderId = "codex",
		}, CancellationToken.None);
		Assert.True(result.Ok);
		return host;
	}

	private static bool HasPaneMessage(FakeHostBridge bridge, string slot, string type, string? text) {
		foreach (var posted in bridge.PostedOfType("agent-pane")) {
			if (posted.GetProperty("slot").GetString() != slot) {
				continue;
			}

			var message = posted.GetProperty("message");
			if (message.GetProperty("type").GetString() == type
				&& (text is null || message.GetProperty("text").GetString() == text)) {
				return true;
			}
		}

		return false;
	}

	private static string[] TranscriptFiles(TestHost host) {
		string dir = WeaviePaths.WorkspaceAgentPanesDir(WorkspaceId.ForPath(host.RepoRoot));
		return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : [];
	}

	[Fact]
	public async Task CodexPaneTranscript_SurvivesWorkerRestart() {
		await using var host = await StartWithCodexSessionAsync("codex-branch");
		host.Send("""{"type":"agent-submit","slot":"codex-branch","prompt":"hello"}""");
		Assert.True(HasPaneMessage(host.Bridge, "codex-branch", "item-completed", "echo: hello"));

		await host.RestartAsync();

		// The fresh session comes up with no live turn; the completed result is present only if it was persisted
		// and replayed (ReplayPane) — proving the transcript survived teardown.
		Assert.True(HasPaneMessage(host.Bridge, "codex-branch", "user-message", "hello"));
		Assert.True(HasPaneMessage(host.Bridge, "codex-branch", "item-completed", "echo: hello"));
	}

	[Fact]
	public async Task ThreadReset_ClearsTranscript_SoRestartComesUpEmpty() {
		await using var host = await StartWithCodexSessionAsync("codex-branch");
		host.Send("""{"type":"agent-submit","slot":"codex-branch","prompt":"hello"}""");
		Assert.Single(TranscriptFiles(host));

		host.Send($$"""{"type":"agent-submit","slot":"codex-branch","prompt":"{{FakeCodexAgentProvider.ResetPrompt}}"}""");

		Assert.NotNull(host.Bridge.LastOfType("agent-pane-reset"));
		Assert.Empty(TranscriptFiles(host)); // the stale transcript file is removed

		await host.RestartAsync();

		Assert.False(HasPaneMessage(host.Bridge, "codex-branch", "item-completed", "echo: hello"));
	}
}
