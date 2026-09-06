using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Configuration;
using Weavie.Core.Hooks;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Learn-from-corrections over a real <see cref="HostCore"/>: hook-driven agent turns whose output the user
/// then edits in the editor (an <c>fs-write</c>, captured at the save) accumulate in the workspace ring, the
/// <c>corrections.learn</c> card surfaces at the threshold, and /learn (weavie.learn.fromCorrections) pre-fills
/// the analysis into the workspace session's Claude pane as a bracketed paste with no trailing submit, then
/// consumes the ring (card gone). An empty ring fails loudly.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreLearnTests {
	[Fact]
	public async Task CorrectionsAccumulate_CardSurfaces_AndLearnSeedsPrimaryClaude_ThenConsumesRing() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(CorrectionsSettings.Enabled, JsonSerializer.SerializeToElement(true));
		var session = host.SelectedSession;
		session.Claude!.EnsureStarted();
		var claude = Assert.Single(host.Platform.NoopLauncher.Created);
		string file = Path.Combine(host.RepoRoot, "app.cs");
		host.Settings.Set("corrections.learnThreshold", JsonDocument.Parse("3").RootElement);

		// Four corrected turns: the agent edits, then the user edits the agent's output in the editor — an
		// fs-write that captures the correction at the save (not a boundary). Each turn is a distinct region, so
		// the four stay four (coalescing only collapses re-saves of one region); three meets the pinned threshold.
		for (int turn = 1; turn <= 4; turn++) {
			Boundary(session, $"prompt {turn}");
			AgentEdit(session, file, $"agent version {turn}\n");
			await HandEditAsync(host, session, file, $"user version {turn}\n");
			if (turn == 3) {
				await WaitForSuggestionAsync(host, present: true);
			}
		}

		string ringPath = WeaviePaths.WorkspaceCorrectionsFile(WorkspaceId.ForPath(host.RepoRoot));
		string recorded = File.ReadAllText(ringPath);
		host.Settings.Set(CorrectionsSettings.Enabled, JsonSerializer.SerializeToElement(false));
		await WaitForSuggestionAsync(host, present: false);
		Boundary(session, "disabled prompt");
		AgentEdit(session, file, "disabled agent version\n");
		await HandEditAsync(host, session, file, "disabled user version\n");
		await session.FileActivity.DrainAsync(CancellationToken.None);
		var disabled = await host.InvokeClientCommandAsync("weavie.learn.fromCorrections", new { });
		Assert.False(disabled.Ok);
		Assert.Contains("disabled", disabled.Error, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(recorded, File.ReadAllText(ringPath));
		Assert.Equal(string.Empty, claude.WrittenText);
		host.Settings.Set(CorrectionsSettings.Enabled, JsonSerializer.SerializeToElement(true));
		await WaitForSuggestionAsync(host, present: true);
		var result = await host.InvokeClientCommandAsync("weavie.learn.fromCorrections", new { });

		string written = claude.WrittenText;
		Assert.StartsWith("\x1b[200~", written); // bracketed paste: one paste, not typed line-by-line
		Assert.EndsWith("\x1b[201~", written); // no trailing CR/LF — nothing is auto-submitted
		Assert.Contains("## Correction 4", written, StringComparison.Ordinal); // recorded at its save, like the rest
		Assert.Contains("prompt 1", written, StringComparison.Ordinal);
		Assert.Contains("-agent version 1", written, StringComparison.Ordinal);
		Assert.Contains("+user version 1", written, StringComparison.Ordinal);

		Assert.True(result.Ok, result.Error);
		// The read entries were consumed: the persisted ring is empty and the card withdrew.
		Assert.Equal(string.Empty, File.ReadAllText(ringPath));
		await WaitForSuggestionAsync(host, present: false);
	}

	[Fact]
	public async Task EmptyRing_LearnFailsLoudly_AndSeedsNothing() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(CorrectionsSettings.Enabled, JsonSerializer.SerializeToElement(true));
		host.SelectedSession.Claude!.EnsureStarted();
		var claude = Assert.Single(host.Platform.NoopLauncher.Created);

		var result = await host.InvokeClientCommandAsync("weavie.learn.fromCorrections", new { });

		Assert.False(result.Ok);
		Assert.Contains("No corrections recorded", result.Error, StringComparison.Ordinal);
		Assert.Equal(string.Empty, claude.WrittenText);
	}

	[Fact]
	public async Task DefaultDisabled_IgnoresCorrections_AndRejectsLearn() {
		await using var host = await TestHost.StartAsync();
		Assert.Equal(false, host.Settings.Resolve(CorrectionsSettings.Enabled).Value);
		var session = host.SelectedSession;
		string file = Path.Combine(host.RepoRoot, "app.cs");
		Boundary(session, "default-disabled prompt");
		AgentEdit(session, file, "agent version\n");
		await HandEditAsync(host, session, file, "user version\n");
		await session.FileActivity.DrainAsync(CancellationToken.None);
		Assert.False(File.Exists(WeaviePaths.WorkspaceCorrectionsFile(WorkspaceId.ForPath(host.RepoRoot))));
		await WaitForSuggestionAsync(host, present: false);
		var result = await host.InvokeClientCommandAsync("weavie.learn.fromCorrections", new { });
		Assert.False(result.Ok);
		Assert.Contains("disabled", result.Error, StringComparison.OrdinalIgnoreCase);
	}

	private static void Boundary(HostSession session, string prompt) =>
		session.ObserveHook(new HookRequest {
			Event = HookEventKind.UserPromptSubmit,
			ToolName = string.Empty,
			ToolInputJson = "{}",
			SessionId = "claude-1",
			Prompt = prompt,
		});

	private static void AgentEdit(HostSession session, string file, string content) {
		var edit = new HookRequest {
			Event = HookEventKind.PreToolUse,
			ToolName = "Edit",
			ToolInputJson = JsonSerializer.Serialize(new { file_path = file }),
			SessionId = "claude-1",
		};
		session.ObserveHook(edit);
		File.WriteAllText(file, content);
		session.ObserveHook(edit with { Event = HookEventKind.PostToolUse });
	}

	// The user editing the agent's output in the editor: an fs-write (as the file provider posts on autosave),
	// which writes disk AND captures the correction at the save.
	private static async Task HandEditAsync(
		TestHost host,
		HostSession session,
		string file,
		string content) {
		var result = await host.SessionRequestAsync<JsonElement>(
			session,
			"files",
			"write",
			new { path = file, content });
		Assert.True(result.GetProperty("ok").GetBoolean());
	}

	// Polls the ambient `suggestions` pushes for the corrections.learn card's presence/absence — the pushes
	// ride the corpus's Changed event, so the state settles asynchronously.
	private static async Task WaitForSuggestionAsync(TestHost host, bool present) {
		for (int attempt = 0; attempt < 50; attempt++) {
			var last = host.Bridge.LastEvent("suggestions", "changed");
			if (last is { } push && push.GetProperty("items").EnumerateArray()
				.Any(item => item.GetProperty("id").GetString() == "corrections.learn") == present) {
				return;
			}

			await Task.Delay(100);
		}

		throw new InvalidOperationException($"corrections.learn never became {(present ? "present" : "absent")}");
	}
}
