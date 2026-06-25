using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The host's keep paths (web <c>keep-hunk</c> / <c>keep-file</c>): advance Core's review baseline so the kept
/// change leaves the pending diff for good — without touching disk — and re-emit the trimmed review set. The
/// durability + slicing is covered by SessionChangeTracker; here we assert the host wires the messages through.
/// </summary>
public sealed class TurnKeepTests {
	[Fact]
	public async Task KeepFile_AdvancesBaseline_LeavingFileFadedInReviewSet() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest() ?? throw new InvalidOperationException("no active session");
		string path = Path.Combine(host.RepoRoot, "readme.txt");

		session.Changes.CaptureBaseline(path);
		File.WriteAllText(path, "hello\nworld\n");
		session.Changes.RecordChange(path);
		Assert.Single(session.Changes.TurnChanges());

		host.Bridge.Clear();
		host.Send($$"""{"type":"keep-file","path":{{JsonSerializer.Serialize(path)}}}""");

		Assert.Equal("hello\nworld\n", File.ReadAllText(path)); // disk untouched — keep is not a revert
		// The file stays in the review set as a faded accepted band (no pending hunks) until keep-all commits it.
		var turn = session.Changes.GetTurn(path);
		Assert.NotNull(turn);
		Assert.Equal(turn!.BaselineText, turn.CurrentText);     // review baseline == current → nothing bright/pending
		Assert.NotEqual(turn.AcceptedBaselineText, turn.CurrentText); // accepted anchor still behind → faded band remains
		Assert.NotNull(host.Bridge.LastOfType("turn-changes")); // the review set was re-emitted
	}

	[Fact]
	public async Task KeepHunk_AdvancesBaseline_OverJustThatHunk() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest() ?? throw new InvalidOperationException("no active session");
		string path = Path.Combine(host.RepoRoot, "readme.txt");

		session.Changes.CaptureBaseline(path); // baseline = "hello\n"
		File.WriteAllText(path, "hello\nworld\n"); // one added hunk: line 2
		session.Changes.RecordChange(path);
		Assert.Single(session.Changes.TurnChanges());

		host.Bridge.Clear();
		host.Send($$"""
			{"type":"keep-hunk","path":{{JsonSerializer.Serialize(path)}},"baselineStart":2,"baselineEndExclusive":2,"currentStart":2,"currentEndExclusive":3,"guardText":"world"}
			""");

		Assert.Equal("hello\nworld\n", File.ReadAllText(path)); // disk untouched
		// The only hunk is now faded-accepted: no pending diff (review baseline == current), but the file stays.
		var turn = session.Changes.GetTurn(path);
		Assert.NotNull(turn);
		Assert.Equal(turn!.BaselineText, turn.CurrentText);
		Assert.NotEqual(turn.AcceptedBaselineText, turn.CurrentText);
		Assert.NotNull(host.Bridge.LastOfType("turn-changes"));
	}

	[Fact]
	public async Task UnkeepHunk_ReturnsAKeptHunkToThePendingBand() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest() ?? throw new InvalidOperationException("no active session");
		string path = Path.Combine(host.RepoRoot, "readme.txt");

		session.Changes.CaptureBaseline(path); // accepted anchor = "hello\n"
		File.WriteAllText(path, "hello\nworld\n"); // one added hunk: line 2
		session.Changes.RecordChange(path);
		Assert.True(session.Changes.KeepHunk(path, new Weavie.Core.Changes.LineRange(2, 2), new Weavie.Core.Changes.LineRange(2, 3), "world"));
		Assert.Equal(session.Changes.GetTurn(path)!.BaselineText, session.Changes.GetTurn(path)!.CurrentText); // faded only

		host.Bridge.Clear();
		// The faded band is the accepted→review insertion: accepted range [2,2) (empty), review range [2,3) ("world").
		host.Send($$"""
			{"type":"unkeep-hunk","path":{{JsonSerializer.Serialize(path)}},"acceptedStart":2,"acceptedEndExclusive":2,"reviewStart":2,"reviewEndExclusive":3,"guardText":"world"}
			""");

		Assert.Equal("hello\nworld\n", File.ReadAllText(path)); // disk untouched — un-keep only moves the band
		var turn = session.Changes.GetTurn(path);
		Assert.NotNull(turn);
		Assert.Equal("hello\n", turn!.BaselineText);             // review baseline rolled back to the anchor → bright again
		Assert.NotNull(host.Bridge.LastOfType("turn-diff"));
	}

	[Fact]
	public async Task KeepHunk_GuardMismatch_LeavesReviewSetIntact() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest() ?? throw new InvalidOperationException("no active session");
		string path = Path.Combine(host.RepoRoot, "readme.txt");

		session.Changes.CaptureBaseline(path);
		File.WriteAllText(path, "hello\nworld\n");
		session.Changes.RecordChange(path);

		host.Bridge.Clear();
		// guardText doesn't match the file's current line 2 — a stale request that must not advance the baseline.
		host.Send($$"""
			{"type":"keep-hunk","path":{{JsonSerializer.Serialize(path)}},"baselineStart":2,"baselineEndExclusive":2,"currentStart":2,"currentEndExclusive":3,"guardText":"STALE"}
			""");

		Assert.Single(session.Changes.TurnChanges()); // still pending — the guard aborted the keep
	}
}
