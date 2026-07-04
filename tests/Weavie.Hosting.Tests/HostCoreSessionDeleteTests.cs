using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Guards issue #217: a programmatic <c>delete</c> (and its <c>classify</c> mode) must target an EXPLICIT id and
/// must NOT fall back to the focused session — an embedded Claude lives in one session while the user may have
/// another focused, so a no-arg delete could tear down a session the caller never intended. <c>unload</c> is the
/// deliberate exception (it keeps its focused-session default — reversible, and the palette's action), asserted
/// here too so the split stays intentional. Runs against a real <see cref="HostCore"/> over a temp git repo.
/// Requires <c>git</c> on PATH.
/// </summary>
[Collection("host-integration")]
public sealed class HostCoreSessionDeleteTests {
	private static IReadOnlyList<string?> SessionIds(TestHost host) {
		var list = host.Bridge.LastOfType("session-list");
		return list is null
			? []
			: [.. list.Value.GetProperty("sessions").EnumerateArray().Select(s => s.GetProperty("id").GetString())];
	}

	[Fact]
	public async Task Delete_WithoutId_IsRejected_AndTouchesNoSession() {
		await using var host = await TestHost.StartAsync();
		// A feature session is created and focused — the very session a no-arg delete used to hit.
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		Assert.Contains("feature", SessionIds(host));

		var result = await host.Core.DeleteSessionAsync(sessionId: null, force: false, CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains("currentSession", result.Error!);
		// The focused session survives — the rejection didn't tear anything down.
		Assert.Contains("feature", SessionIds(host));
	}

	[Fact]
	public async Task Unload_WithoutId_ParksTheActiveSession() {
		// Unlike delete, unload keeps its focused-session default — it's the palette's "Unload Session" action and
		// is reversible. A no-arg unload parks the ACTIVE (feature) session; it must NOT be rejected.
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);

		var result = await host.Core.UnloadSessionAsync(sessionId: null, CancellationToken.None);

		Assert.True(result.Ok, result.Error);
		// The feature chip survives on the rail but is now dormant (unloaded), not removed.
		var feature = host.Bridge.LastOfType("session-list")!.Value.GetProperty("sessions")
			.EnumerateArray().Single(s => s.GetProperty("id").GetString() == "feature");
		Assert.False(feature.GetProperty("loaded").GetBoolean());
	}

	[Fact]
	public async Task Classify_WithoutId_IsRejected() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);

		var result = await host.Core.ClassifyDeleteAsync(sessionId: null, CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains("currentSession", result.Error!);
	}

	[Fact]
	public async Task Delete_UnknownId_ReportsNoSuchSession() {
		await using var host = await TestHost.StartAsync();

		var result = await host.Core.DeleteSessionAsync("no-such-branch", force: false, CancellationToken.None);

		Assert.False(result.Ok);
		Assert.Contains("No such session", result.Error!);
	}

	[Fact]
	public async Task Delete_ByExplicitId_RemovesThatSessionFromTheRail() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		Assert.Contains("feature", SessionIds(host));

		var result = await host.Core.DeleteSessionAsync("feature", force: false, CancellationToken.None);

		Assert.True(result.Ok, result.Error);
		Assert.DoesNotContain("feature", SessionIds(host));
	}
}
