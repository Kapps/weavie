using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The host's keep paths (web <c>keep-hunk</c> / <c>keep-file</c>): advance Core's review baseline so the kept
/// change leaves the pending diff for good — without touching disk — and re-emit the trimmed review set. The
/// durability + slicing is covered by SessionChangeTracker; here we assert the host wires the messages through.
/// </summary>
[Collection(TestCollections.HostIntegration)]
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

		// The file stays in the review set as a faded accepted band (no pending hunks) until keep-all commits it.
		Assert.Equal("hello\nworld\n", File.ReadAllText(path)); // disk untouched — keep is not a revert
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

		// The only hunk is now faded-accepted: no pending diff (review baseline == current), but the file stays.
		Assert.Equal("hello\nworld\n", File.ReadAllText(path)); // disk untouched
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
			{"type":"unkeep-hunk","path":{{JsonSerializer.Serialize(path)}},"acceptedStart":2,"acceptedEndExclusive":2,"reviewStart":2,"reviewEndExclusive":3,"acceptedGuardText":"","guardText":"world"}
			""");

		Assert.Equal("hello\nworld\n", File.ReadAllText(path)); // disk untouched — un-keep only moves the band
		var turn = session.Changes.GetTurn(path);
		Assert.NotNull(turn);
		Assert.Equal("hello\n", turn!.BaselineText);             // review baseline rolled back to the anchor → bright again
		Assert.NotNull(host.Bridge.LastOfType("turn-diff"));
	}

	[Fact]
	public async Task NewPrompt_CommitsFadedBand_AndRePushesReviewState() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest() ?? throw new InvalidOperationException("no active session");
		string path = Path.Combine(host.RepoRoot, "readme.txt");

		session.Changes.CaptureBaseline(path);
		File.WriteAllText(path, "hello\nworld\n");
		session.Changes.RecordChange(path);
		host.Send($$"""{"type":"keep-file","path":{{JsonSerializer.Serialize(path)}}}""");
		Assert.Single(session.Changes.TurnChanges()); // kept: faded band only

		host.Bridge.Clear();
		// A new prompt (the UserPromptSubmit hook) commits the accepted band: the file leaves the diff view.
		session.Changes.Observe(new Weavie.Core.Hooks.HookRequest {
			Event = Weavie.Core.Hooks.HookEventKind.UserPromptSubmit,
			ToolName = string.Empty,
			ToolInputJson = "{}",
		});

		Assert.Empty(session.Changes.TurnChanges());
		var files = host.Bridge.LastOfType("turn-changes");
		Assert.NotNull(files);
		Assert.Empty(files!.Value.GetProperty("files").EnumerateArray()); // the trimmed (now empty) review set
		var diff = host.Bridge.LastOfType("turn-diff");
		Assert.NotNull(diff); // the file's inline markers clear: accepted == current
		Assert.Equal(diff!.Value.GetProperty("current").GetString(), diff.Value.GetProperty("acceptedBaseline").GetString());
		var history = host.Bridge.LastOfType("review-history");
		Assert.NotNull(history); // the commit cleared the undo history
		Assert.False(history!.Value.GetProperty("canUndo").GetBoolean());
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
