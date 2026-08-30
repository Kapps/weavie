using Weavie.Core.Diffs;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Workspaces;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The diff lifecycle, including the exact-session guarantee: ids only need to be unique within their owning
/// presenter because the session bus routes every resolution before it reaches that presenter.
/// </summary>
public sealed class McpDiffPresenterTests {
	private static (McpDiffPresenter presenter, FakeHostBridge bridge) NewPresenter() =>
		NewPresenter(out _);

	private static (McpDiffPresenter presenter, FakeHostBridge bridge) NewPresenter(
		out MessageTargetFeature replayTarget) {
		var bridge = new FakeHostBridge();
		var bus = new SessionMessageBus(
			new SessionAddress("test", Guid.NewGuid().ToString("n")),
			bridge.Broadcast,
			bridge.Send,
			_ => { });
		var channel = bus.Feature("editor");
		replayTarget = bus.BroadcastTarget.Feature("editor");
		var fs = new InMemoryFileSystem();
		var files = new FileProviderService(fs);
		var opener = new FileOpener(
			bridge.SessionViewFeature("view"),
			bridge.SessionFeature("notifications"),
			files,
			new WorkspaceFileIndex(fs, "/ws"),
			(_, _, _, _) => { });
		return (new McpDiffPresenter(channel, files, opener, _ => { }), bridge);
	}

	private static string DiffId(FakeHostBridge bridge) {
		var show = bridge.LastEvent("editor", "showDiff");
		Assert.True(show.HasValue);
		return show!.Value.GetProperty("id").GetString()!;
	}

	private static DiffProposal Proposal(string contents = "proposed") =>
		new("/ws/a.cs", "/ws/a.cs", contents, "tab");

	[Fact]
	public async Task DiffIds_AreScopedToTheirOwningPresenters() {
		var (p1, b1) = NewPresenter();
		var (p2, b2) = NewPresenter();

		var first = p1.PresentDiffAsync(Proposal(), CancellationToken.None);
		var second = p2.PresentDiffAsync(Proposal(), CancellationToken.None);
		string firstId = DiffId(b1);
		string secondId = DiffId(b2);

		Assert.Equal(firstId, secondId);
		Assert.True(p1.Resolve(firstId, kept: true, finalContents: "first"));
		Assert.True(p2.Resolve(secondId, kept: true, finalContents: "second"));
		Assert.Equal("first", (await first).FinalContents);
		Assert.Equal("second", (await second).FinalContents);
	}

	[Fact]
	public async Task Resolve_OnlyTheOwningPresenterAcceptsTheId() {
		var (owner, ownerBridge) = NewPresenter();
		var (other, _) = NewPresenter();
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
		var (presenter, bridge) = NewPresenter();
		var task = presenter.PresentDiffAsync(Proposal(), CancellationToken.None);

		Assert.True(presenter.Resolve(DiffId(bridge), kept: false, finalContents: null));

		var outcome = await task;
		Assert.Equal(DiffResult.Rejected, outcome.Result);
	}

	[Fact]
	public void Resolve_ClosesOnlyItsExactDiff() {
		var (presenter, bridge) = NewPresenter();
		_ = presenter.PresentDiffAsync(Proposal(), CancellationToken.None);
		string id = DiffId(bridge);
		bridge.Clear();

		Assert.True(presenter.Resolve(id, kept: true, finalContents: "final"));

		Assert.Equal(id, bridge.LastEvent("editor", "closeDiff")!.Value.GetProperty("id").GetString());
	}

	[Fact]
	public void Resolve_UnknownId_ReturnsFalse() {
		var (presenter, _) = NewPresenter();
		Assert.False(presenter.Resolve("diff-does-not-exist", kept: true, finalContents: null));
	}

	[Fact]
	public void ReconnectSnapshotContainsThePendingDiff() {
		var (presenter, bridge) = NewPresenter(out var replayTarget);
		_ = presenter.PresentDiffAsync(Proposal(), CancellationToken.None);
		string id = DiffId(bridge);
		bridge.Clear();

		presenter.Replay(replayTarget);

		var snapshot = bridge.LastEvent("editor", "diffSnapshot");
		var proposal = Assert.Single(snapshot!.Value.GetProperty("proposals").EnumerateArray());
		Assert.Equal(id, proposal.GetProperty("id").GetString());
		Assert.Equal("proposed", proposal.GetProperty("proposed").GetString());
	}

	[Fact]
	public async Task Cancellation_CompletesTheTaskAndStopsTracking() {
		var (presenter, bridge) = NewPresenter();
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
		var (presenter, bridge) = NewPresenter();
		var task = presenter.PresentDiffAsync(Proposal(), CancellationToken.None);
		string id = DiffId(bridge);
		bridge.Clear();

		presenter.DismissPending();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
		var close = bridge.LastEvent("editor", "closeDiff");
		Assert.True(close.HasValue, "a dismissed review must be closed in the page");
		Assert.Equal(id, close!.Value.GetProperty("id").GetString());
		// The entry is gone, so a late resolve finds nothing.
		Assert.False(presenter.Resolve(id, kept: true, finalContents: null));
	}
}
