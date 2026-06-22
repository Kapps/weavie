using Weavie.Core.Diffs;
using Weavie.Core.FileSystem;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The diff lifecycle, with the cross-session guarantees the host depends on: diff ids are unique across ALL
/// sessions (so a <c>diff-resolved</c> can be routed back to the session that owns it), and only the owning
/// presenter resolves a given id (so a switch between render and resolve can't resolve the wrong session's diff).
/// </summary>
public sealed class McpDiffPresenterTests {
	private static (McpDiffPresenter presenter, FakeHostBridge bridge) NewActive() {
		var bridge = new FakeHostBridge();
		var channel = new SessionEditorChannel(bridge);
		channel.Activate(); // active so the show-diff is posted and the test can read its id
		var fs = new InMemoryFileSystem();
		return (new McpDiffPresenter(channel, fs, new FileOpener(channel, fs, "/ws")), bridge);
	}

	private static string DiffId(FakeHostBridge bridge) {
		var show = bridge.LastOfType("show-diff");
		Assert.True(show.HasValue);
		return show!.Value.GetProperty("id").GetString()!;
	}

	private static DiffProposal Proposal(string contents = "proposed") =>
		new("/ws/a.cs", "/ws/a.cs", contents, "tab");

	[Fact]
	public void DiffIds_AreUniqueAcrossPresenters() {
		var (p1, b1) = NewActive();
		var (p2, b2) = NewActive();

		_ = p1.PresentDiffAsync(Proposal(), CancellationToken.None);
		_ = p2.PresentDiffAsync(Proposal(), CancellationToken.None);

		Assert.NotEqual(DiffId(b1), DiffId(b2));
	}

	[Fact]
	public async Task Resolve_OnlyTheOwningPresenterAcceptsTheId() {
		var (owner, ownerBridge) = NewActive();
		var (other, _) = NewActive();
		var task = owner.PresentDiffAsync(Proposal(), CancellationToken.None);
		string id = DiffId(ownerBridge);

		// A different session must NOT be able to resolve another session's diff (the switch-race corruption).
		Assert.False(other.Resolve(id, kept: true, finalContents: "wrong"));
		Assert.True(owner.Resolve(id, kept: true, finalContents: "final"));

		var outcome = await task;
		Assert.Equal(DiffResult.Kept, outcome.Result);
		Assert.Equal("final", outcome.FinalContents);
	}

	[Fact]
	public async Task Resolve_Reject_CompletesAsRejected() {
		var (presenter, bridge) = NewActive();
		var task = presenter.PresentDiffAsync(Proposal(), CancellationToken.None);

		Assert.True(presenter.Resolve(DiffId(bridge), kept: false, finalContents: null));

		var outcome = await task;
		Assert.Equal(DiffResult.Rejected, outcome.Result);
	}

	[Fact]
	public void Resolve_UnknownId_ReturnsFalse() {
		var (presenter, _) = NewActive();
		Assert.False(presenter.Resolve("diff-does-not-exist", kept: true, finalContents: null));
	}

	[Fact]
	public async Task Cancellation_CompletesTheTaskAndStopsTracking() {
		var (presenter, bridge) = NewActive();
		using var cts = new CancellationTokenSource();
		var task = presenter.PresentDiffAsync(Proposal(), cts.Token);
		string id = DiffId(bridge);

		await cts.CancelAsync();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
		// Cancellation removed the pending entry, so a late resolve finds nothing.
		Assert.False(presenter.Resolve(id, kept: true, finalContents: null));
	}

	[Fact]
	public async Task DismissPending_CancelsTheReviewAndClosesItInThePage() {
		// The user flipped Claude into acceptEdits (Shift+Tab) with a default-mode openDiff still showing, so it
		// was never resolved in Weavie. DismissPending tears it down: cancel the awaiting task (the MCP server
		// then sends nothing back) and close the stale review in the page so its transient model can't linger.
		var (presenter, bridge) = NewActive();
		var task = presenter.PresentDiffAsync(Proposal(), CancellationToken.None);
		string id = DiffId(bridge);
		bridge.Clear();

		presenter.DismissPending();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
		var close = bridge.LastOfType("close-diff");
		Assert.True(close.HasValue, "a dismissed review must be closed in the page");
		Assert.Equal(id, close!.Value.GetProperty("id").GetString());
		// The entry is gone, so a late resolve finds nothing.
		Assert.False(presenter.Resolve(id, kept: true, finalContents: null));
	}
}
