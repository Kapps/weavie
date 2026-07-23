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
	public async Task KeepFile_LastPendingFile_ExitsTheReview() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest() ?? throw new InvalidOperationException("no active session");
		string path = Path.Combine(host.RepoRoot, "readme.txt");

		session.Changes.CaptureBaseline(path);
		File.WriteAllText(path, "hello\nworld\n");
		session.Changes.RecordChange(path);
		Assert.Single(session.Changes.TurnChanges());

		host.Bridge.Clear();
		host.Send($$"""{"type":"keep-file","path":{{JsonSerializer.Serialize(path)}}}""");

		// Keeping the set's last pending file settles the review: it commits (as keep-all would) and exits.
		Assert.Equal("hello\nworld\n", File.ReadAllText(path)); // disk untouched — keep is not a revert
		Assert.Empty(session.Changes.TurnChanges());
		var files = host.Bridge.LastOfType("turn-changes");
		Assert.NotNull(files); // the now-empty review set was re-emitted, so the page's toolbar goes away
		Assert.Empty(files!.Value.GetProperty("files").EnumerateArray());
	}

	[Fact]
	public async Task KeepHunk_AdvancesBaseline_OverJustThatHunk() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest() ?? throw new InvalidOperationException("no active session");
		string path = Path.Combine(host.RepoRoot, "readme.txt");

		session.Changes.CaptureBaseline(path); // baseline = "hello\n"
		File.WriteAllText(path, "hello\nworld\nmore\n"); // one added band: lines 2-3
		session.Changes.RecordChange(path);
		Assert.Single(session.Changes.TurnChanges());

		host.Bridge.Clear();
		// Keep only line 2 of the added band; line 3 stays bright, so the review must NOT settle or commit.
		host.Send($$"""
			{"type":"keep-hunk","path":{{JsonSerializer.Serialize(path)}},"baselineStart":2,"baselineEndExclusive":2,"currentStart":2,"currentEndExclusive":3,"guardText":"world"}
			""");

		Assert.Equal("hello\nworld\nmore\n", File.ReadAllText(path)); // disk untouched
		var turn = session.Changes.GetTurn(path);
		Assert.NotNull(turn);
		Assert.Equal("hello\nworld\n", turn!.BaselineText);           // the baseline absorbed just the kept line
		Assert.NotEqual(turn.BaselineText, turn.CurrentText);         // "more" still bright-pending
		Assert.NotEqual(turn.AcceptedBaselineText, turn.BaselineText); // the kept line is a faded band
		Assert.NotNull(host.Bridge.LastOfType("turn-changes"));
	}

	[Fact]
	public async Task KeepHunk_AfterNonAgentHandEdit_KeepsTheAgentHunk() {
		// The reported bug: a hand edit to a region the agent didn't author shifts the agent hunk's line number,
		// and the web (which diffs the live model) sends that shifted position. The keep must not fail its guard.
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest() ?? throw new InvalidOperationException("no active session");
		string path = Path.Combine(host.RepoRoot, "readme.txt");

		session.Changes.CaptureBaseline(path); // baseline = "hello\n"
		File.WriteAllText(path, "hello\nAGENT\n"); // agent adds line 2
		session.Changes.RecordChange(path);

		// User prepends an unrelated line and autosaves — the fs-write handler records the hand edit. "AGENT" is now
		// live-model line 3; _current stays "hello\nAGENT\n" because the insertion isn't over an agent line.
		File.WriteAllText(path, "MINE\nhello\nAGENT\n");
		session.Changes.RecordHandEdit(path, "MINE\nhello\nAGENT\n");

		host.Bridge.Clear();
		host.Send($$"""
			{"type":"keep-hunk","path":{{JsonSerializer.Serialize(path)}},"baselineStart":2,"baselineEndExclusive":2,"currentStart":3,"currentEndExclusive":4,"guardText":"AGENT"}
			""");

		Assert.Equal("MINE\nhello\nAGENT\n", File.ReadAllText(path)); // disk untouched — keep is not a write
		var turn = session.Changes.GetTurn(path);
		Assert.NotNull(turn);
		Assert.Equal(turn!.BaselineText, turn.CurrentText); // the agent hunk is now accepted — no pending band left
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
		string pending = Path.Combine(host.RepoRoot, "notes.txt");

		session.Changes.CaptureBaseline(path);
		File.WriteAllText(path, "hello\nworld\n");
		session.Changes.RecordChange(path);
		// A second file stays bright so the keep below leaves a faded band instead of settling the review.
		session.Changes.CaptureBaseline(pending);
		File.WriteAllText(pending, "note\n");
		session.Changes.RecordChange(pending);
		host.Send($$"""{"type":"keep-file","path":{{JsonSerializer.Serialize(path)}}}""");
		Assert.Equal(2, session.Changes.TurnChanges().Count); // kept file faded + pending file bright

		host.Bridge.Clear();
		// A new prompt (the UserPromptSubmit hook) commits the accepted band: the kept file leaves the diff view.
		session.Changes.Observe(new Weavie.Core.Hooks.HookRequest {
			Event = Weavie.Core.Hooks.HookEventKind.UserPromptSubmit,
			ToolName = string.Empty,
			ToolInputJson = "{}",
		});

		var remaining = Assert.Single(session.Changes.TurnChanges());
		Assert.Equal(pending, remaining.Path);
		var files = host.Bridge.LastOfType("turn-changes");
		Assert.NotNull(files); // the trimmed review set: only the still-pending file
		Assert.Single(files!.Value.GetProperty("files").EnumerateArray());
		var diff = host.Bridge.LastOfType("turn-diff");
		Assert.NotNull(diff); // the kept file's inline markers clear: accepted == current
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
