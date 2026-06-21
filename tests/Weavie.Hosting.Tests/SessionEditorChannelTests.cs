using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The output-isolation gate: a session may write the page's single editor only while it is the active
/// session; otherwise it HOLDS its open-file/close-tab and its blocking openDiff, replaying them on switch-in
/// and tearing a live diff out of the page on switch-away. This is what keeps a background Claude from
/// rendering over the foreground session (the isolation bug) without silently dropping its work.
/// </summary>
public sealed class SessionEditorChannelTests {
	private static string TypeOf(string json) {
		using var doc = JsonDocument.Parse(json);
		return doc.RootElement.GetProperty("type").GetString() ?? "";
	}

	[Fact]
	public void Muted_BuffersReveals_ThenReplaysInOrderOnActivate() {
		var bridge = new FakeHostBridge();
		var channel = new SessionEditorChannel(bridge); // created muted

		channel.Reveal("""{"type":"open-file","path":"a"}""");
		channel.Reveal("""{"type":"open-file","path":"b"}""");
		Assert.Empty(bridge.Posted); // nothing leaks while muted

		channel.Activate();

		Assert.Equal(2, bridge.Posted.Count);
		Assert.Equal("a", JsonDocument.Parse(bridge.Posted[0]).RootElement.GetProperty("path").GetString());
		Assert.Equal("b", JsonDocument.Parse(bridge.Posted[1]).RootElement.GetProperty("path").GetString());
	}

	[Fact]
	public void Active_PostsRevealImmediately() {
		var bridge = new FakeHostBridge();
		var channel = new SessionEditorChannel(bridge);
		channel.Activate();

		channel.Reveal("""{"type":"open-file","path":"a"}""");

		Assert.Single(bridge.Posted);
	}

	[Fact]
	public void HeldDiff_SurfacesOnActivate() {
		var bridge = new FakeHostBridge();
		var channel = new SessionEditorChannel(bridge);

		channel.ShowDiff("diff-7", """{"type":"show-diff","id":"diff-7"}""");
		Assert.Empty(bridge.Posted); // a background session's diff is held, not posted into the foreground

		channel.Activate();

		string shown = Assert.Single(bridge.Posted);
		Assert.Equal("show-diff", TypeOf(shown));
	}

	[Fact]
	public void DeactivateWithLiveDiff_TearsItOutOfThePage() {
		var bridge = new FakeHostBridge();
		var channel = new SessionEditorChannel(bridge);
		channel.Activate();
		channel.ShowDiff("diff-7", """{"type":"show-diff","id":"diff-7"}""");
		bridge.Clear();

		channel.Deactivate();

		string close = Assert.Single(bridge.Posted);
		using var doc = JsonDocument.Parse(close);
		Assert.Equal("close-diff", doc.RootElement.GetProperty("type").GetString());
		Assert.Equal("diff-7", doc.RootElement.GetProperty("id").GetString());
	}

	[Fact]
	public void HeldDiff_SurvivesSwitchAwayAndBack() {
		var bridge = new FakeHostBridge();
		var channel = new SessionEditorChannel(bridge);
		channel.Activate();
		channel.ShowDiff("diff-7", """{"type":"show-diff","id":"diff-7"}""");

		channel.Deactivate(); // close-diff
		bridge.Clear();
		channel.Activate(); // re-render the still-unresolved diff

		string reshown = Assert.Single(bridge.Posted);
		Assert.Equal("show-diff", TypeOf(reshown));
	}

	[Fact]
	public void EndDiff_StopsTracking_SoLaterDeactivateDoesNotReshow() {
		var bridge = new FakeHostBridge();
		var channel = new SessionEditorChannel(bridge);
		channel.Activate();
		channel.ShowDiff("diff-7", """{"type":"show-diff","id":"diff-7"}""");

		channel.EndDiff("diff-7", closeInUi: false); // resolved by the user; page already closed it
		bridge.Clear();

		channel.Deactivate(); // nothing live → no close-diff
		channel.Activate(); // nothing held → no re-show
		Assert.Empty(bridge.Posted);
	}

	[Fact]
	public void EndDiff_ForADifferentId_IsANoOp() {
		var bridge = new FakeHostBridge();
		var channel = new SessionEditorChannel(bridge);
		channel.Activate();
		channel.ShowDiff("diff-7", """{"type":"show-diff","id":"diff-7"}""");
		bridge.Clear();

		channel.EndDiff("diff-other", closeInUi: true); // not the live diff
		Assert.Empty(bridge.Posted);

		// The real diff is still tracked: a deactivate still tears it out.
		channel.Deactivate();
		Assert.Equal("close-diff", TypeOf(Assert.Single(bridge.Posted)));
	}

	[Fact]
	public void Activate_IsIdempotent() {
		var bridge = new FakeHostBridge();
		var channel = new SessionEditorChannel(bridge);
		channel.Reveal("""{"type":"open-file","path":"a"}""");
		channel.Activate();
		bridge.Clear();

		channel.Activate(); // already active — must not re-replay

		Assert.Empty(bridge.Posted);
	}
}
