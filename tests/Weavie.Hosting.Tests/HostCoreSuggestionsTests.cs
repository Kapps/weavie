using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// End-to-end wiring for the contextual-suggestions surface: the page's <c>ready</c> pushes the active set,
/// and a <c>dismiss-suggestion</c> message re-pushes. The test repo carries no dependency manifest, so the
/// worktree-setup card is correctly absent — the assertions cover the host plumbing, not relevance (which
/// <c>SuggestionServiceTests</c> covers).
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSuggestionsTests {
	[Fact]
	public async Task Ready_PushesTheActiveSuggestionSet() {
		await using var host = await TestHost.StartAsync();

		var pushed = host.Bridge.LastEvent("suggestions", "changed");

		Assert.True(pushed.HasValue);
		Assert.Empty(pushed!.Value.GetProperty("items").EnumerateArray()); // no manifest in the test repo
	}

	[Fact]
	public async Task DismissSuggestion_RePushesTheActiveSet() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Clear();

		// forever:false → snooze (in-memory), so this asserts the dispatch → service → re-push wiring without
		// writing a dismissals file.
		host.HostEvent(
			"suggestions",
			"dismiss",
			new { id = "worktree.setupCommand", forever = false });

		Assert.NotNull(host.Bridge.LastEvent("suggestions", "changed"));
	}
}
