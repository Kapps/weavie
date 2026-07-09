using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The host's file-scoped revert (web <c>revert-file</c>): restores the whole file to its turn baseline on disk
/// and re-emits the trimmed review set, so the now-clean file leaves the review walk. Mirrors the per-hunk
/// reject + whole-turn undo paths scoped to one path; the underlying restore is covered by SessionChangeTracker.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class TurnRevertTests {
	[Fact]
	public async Task RevertFile_RestoresBaseline_AndDropsFileFromReviewSet() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest() ?? throw new InvalidOperationException("no active session");
		string path = Path.Combine(host.RepoRoot, "readme.txt");

		// Seed a tracked change: baseline = current disk ("hello\n"), an edit lands, then current is recorded.
		session.Changes.CaptureBaseline(path);
		File.WriteAllText(path, "hello\nworld\n");
		session.Changes.RecordChange(path);
		Assert.Single(session.Changes.TurnChanges());

		host.Bridge.Clear();
		host.Send($$"""{"type":"revert-file","path":{{JsonSerializer.Serialize(path)}}}""");

		Assert.Equal("hello\n", File.ReadAllText(path));
		Assert.Empty(session.Changes.TurnChanges());
		Assert.NotNull(host.Bridge.LastOfType("turn-changes")); // the trimmed review set was re-emitted
	}
}
