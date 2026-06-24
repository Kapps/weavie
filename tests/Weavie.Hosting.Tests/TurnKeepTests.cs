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
	public async Task KeepFile_AdvancesBaseline_AndDropsFileFromReviewSet() {
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
		Assert.Empty(session.Changes.TurnChanges());            // the file left the review set
		Assert.NotNull(host.Bridge.LastOfType("turn-changes")); // the trimmed review set was re-emitted
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
		Assert.Empty(session.Changes.TurnChanges());            // the only hunk was kept → file left the set
		Assert.NotNull(host.Bridge.LastOfType("turn-changes"));
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
