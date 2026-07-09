using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The workspace-setup nudge over a real <see cref="HostCore"/>: a repo with a build manifest and no configured
/// settings surfaces the card, and taking it ("Yes" → weavie.workspace.setup) pre-fills the setup prompt into
/// the primary session's Claude pane as a bracketed paste with no trailing submit.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreWorkspaceSetupTests {
	[Fact]
	public async Task ManifestRepo_SurfacesCard_AndYesSeedsClaudePane_WithoutSubmitting() {
		await using var host = await TestHost.StartAsync(repo =>
			File.WriteAllText(Path.Combine(repo, "package.json"), "{}"));

		// The manifest probe is async; wait for the suggestions push that offers workspace.setup.
		await WaitForSuggestionAsync(host, "workspace.setup");

		host.Core.ActiveSessionForTest()!.Claude!.EnsureStarted();
		var claude = Assert.Single(host.Platform.NoopLauncher.Created);

		host.Send("{\"type\":\"invoke-command\",\"id\":\"weavie.workspace.setup\"}");

		string written = claude.WrittenText;
		Assert.StartsWith("\x1b[200~", written); // bracketed paste: treated as one paste, not typed line-by-line
		Assert.EndsWith("\x1b[201~", written); // ends at the paste marker — no trailing CR/LF, so nothing is submitted
		Assert.Contains("test.profile", written, StringComparison.Ordinal);
		Assert.Contains("worktree.setupCommand", written, StringComparison.Ordinal);
	}

	private static async Task WaitForSuggestionAsync(TestHost host, string id) {
		for (int attempt = 0; attempt < 50; attempt++) {
			var last = host.Bridge.LastOfType("suggestions");
			if (last is { } push && push.GetProperty("items").EnumerateArray()
				.Any(item => item.GetProperty("id").GetString() == id)) {
				return;
			}

			await Task.Delay(100);
		}

		throw new InvalidOperationException($"suggestion '{id}' never appeared");
	}
}
