using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The terminal ref-link push: the host resolves each session's <c>origin</c> to its
/// forge issue/PR URL prefix so a terminal <c>#N</c> links to its page, or pushes null when origin isn't a forge
/// repo (so <c>#N</c> stays plain text). Requires <c>git</c> on PATH. See <c>HostCore.RefLinks.cs</c>.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreRefLinksTests {
	[Fact]
	public async Task RefLinkBase_PushesForgePullPrefix_ForAGitHubOrigin() {
		await using var host = await TestHost.StartAsync(repo =>
			TempGitRepo.Run(repo, "remote", "add", "origin", "git@github.com:acme/demo.git"));

		var msg = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(host.WorkspaceSession.Address, "git", "refLinkBase"));
		Assert.Equal("https://github.com/acme/demo/pull/", msg.GetProperty("prefix").GetString());
	}

	[Fact]
	public async Task RefLinkBase_PushesNull_WhenOriginIsntAForgeRepo() {
		// The default test repo has no 'origin' remote, so a terminal #N isn't linkable — the honest null state.
		await using var host = await TestHost.StartAsync();

		var msg = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(host.WorkspaceSession.Address, "git", "refLinkBase"));
		Assert.Equal(JsonValueKind.Null, msg.GetProperty("prefix").ValueKind);
	}
}
