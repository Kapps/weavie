using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The host's file-scoped revert (web <c>revert-file</c>): restores the whole file to its turn baseline on disk
/// and retains the rejected proposal in the review set. Mirrors the per-hunk
/// reject + whole-turn undo paths scoped to one path; the underlying restore is covered by SessionChangeTracker.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class TurnRevertTests {
	[Fact]
	public async Task RevertFile_RestoresBaseline_AndRetainsTheRejectedProposal() {
		await using var host = await TestHost.StartAsync();
		var session = host.SelectedSession;
		string path = Path.Combine(host.RepoRoot, "readme.txt");

		// Seed a tracked change: baseline = current disk ("hello\n"), an edit lands, then current is recorded.
		session.Changes.CaptureBaseline(path);
		File.WriteAllText(path, "hello\nworld\n");
		session.Changes.RecordChange(path);
		Assert.Single(session.Changes.TurnChanges());

		host.Bridge.Clear();
		host.SessionEvent(session, "review", "revertFile", new { path });
		await session.FileActivity.DrainAsync(CancellationToken.None);

		Assert.Equal("hello\n", File.ReadAllText(path));
		var review = session.Changes.GetTurn(path)!;
		Assert.Equal(review.BaselineText, review.CurrentText);
		Assert.Equal("world", Assert.Single(review.Rejected).Text);
		Assert.NotNull(host.Bridge.LastEvent(session.Address, "review", "changes"));
	}
}
