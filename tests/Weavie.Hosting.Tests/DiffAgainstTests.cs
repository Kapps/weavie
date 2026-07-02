using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The "diff against &lt;ref&gt;" flow (web <c>diff-against</c>): resolve the ref, diff the working tree from
/// its merge-base with HEAD, and feed the review navigator over <c>pr-changes</c> / <c>pr-diff</c> — or answer
/// with a toast when the ref is unknown or nothing differs. See docs/specs/diff-against.md.
/// </summary>
public sealed class DiffAgainstTests {
	[Fact]
	public async Task DiffAgainstHead_PushesUncommittedChanges_AndServesTheFileDiff() {
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		File.WriteAllText(path, "hello\nworld\n"); // committed content is "hello\n"

		host.Send("""{"type":"diff-against","ref":"HEAD"}""");

		var changes = await Wait.ForAsync(() => host.Bridge.LastOfType("pr-changes"));
		Assert.Equal(0, changes.GetProperty("number").GetInt32());
		Assert.Equal("vs HEAD", changes.GetProperty("label").GetString());
		var files = changes.GetProperty("files").EnumerateArray().ToList();
		var file = Assert.Single(files);
		Assert.Equal(path, file.GetProperty("path").GetString());
		Assert.Equal(1, file.GetProperty("added").GetInt32());

		// The per-file diff pairs the merge-base content (baseline) with the worktree file (current).
		host.Send($$"""{"type":"get-pr-diff","number":0,"path":{{JsonSerializer.Serialize(path)}}}""");
		var diff = await Wait.ForAsync(() => host.Bridge.LastOfType("pr-diff"));
		Assert.Equal("hello\n", diff.GetProperty("baseline").GetString());
		Assert.Equal("hello\nworld\n", diff.GetProperty("current").GetString());
		Assert.Empty(diff.GetProperty("comments").EnumerateArray()); // no forge on a local ref diff
	}

	[Fact]
	public async Task DiffAgainstParent_ShowsTheLastCommitPlusUncommitted() {
		await using var host = await TestHost.StartAsync();
		File.WriteAllText(Path.Combine(host.RepoRoot, "readme.txt"), "hello\nagain\n");
		Git(host.RepoRoot, "add", "-A");
		Git(host.RepoRoot, "-c", "user.email=test@weavie.dev", "-c", "user.name=Weavie Test", "-c", "commit.gpgsign=false", "commit", "-m", "second");
		File.WriteAllText(Path.Combine(host.RepoRoot, "extra.txt"), "new\n");

		host.Send("""{"type":"diff-against","ref":"HEAD^"}""");

		var changes = await Wait.ForAsync(() => host.Bridge.LastOfType("pr-changes"));
		Assert.Equal("vs HEAD^", changes.GetProperty("label").GetString());
		var names = changes.GetProperty("files").EnumerateArray()
			.Select(f => f.GetProperty("name").GetString()).ToList();
		Assert.Contains("readme.txt", names); // the last commit's change
		Assert.Contains("extra.txt", names);  // the uncommitted, still-untracked addition
	}

	[Fact]
	public async Task DiffAgainstHead_SurfacesAnUntrackedFile_AsAllAdded() {
		await using var host = await TestHost.StartAsync();
		File.WriteAllText(Path.Combine(host.RepoRoot, "brand-new.txt"), "one\ntwo\n");

		host.Send("""{"type":"diff-against","ref":"HEAD"}""");

		// A brand-new file IS an uncommitted change — never "No changes against 'HEAD'."
		var changes = await Wait.ForAsync(() => host.Bridge.LastOfType("pr-changes"));
		var file = Assert.Single(changes.GetProperty("files").EnumerateArray());
		Assert.Equal("brand-new.txt", file.GetProperty("name").GetString());
		Assert.Equal(2, file.GetProperty("added").GetInt32());
	}

	[Fact]
	public async Task GetPrDiff_ForAPathOutsideTheReviewWorktree_IsDropped() {
		await using var host = await TestHost.StartAsync();
		File.WriteAllText(Path.Combine(host.RepoRoot, "readme.txt"), "hello\nworld\n");
		host.Send("""{"type":"diff-against","ref":"HEAD"}""");
		await Wait.ForAsync(() => host.Bridge.LastOfType("pr-changes"));

		// Local reviews all carry number 0, so the number can't disambiguate — a request whose path escapes
		// the active review's worktree (a switch-race leftover) must be dropped, never resolved against it.
		string foreign = Path.Combine(Path.GetTempPath(), "weavie-foreign-" + Guid.NewGuid().ToString("n") + ".txt");
		File.WriteAllText(foreign, "elsewhere\n");
		try {
			host.Bridge.Clear();
			host.Send($$"""{"type":"get-pr-diff","number":0,"path":{{JsonSerializer.Serialize(foreign)}}}""");

			await Task.Delay(300);
			Assert.Null(host.Bridge.LastOfType("pr-diff"));
		} finally {
			File.Delete(foreign);
		}
	}

	[Fact]
	public async Task DiffAgainst_UnknownRef_ToastsAndArmsNothing() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Clear();

		host.Send("""{"type":"diff-against","ref":"no-such-ref"}""");

		var toast = await Wait.ForAsync(() => host.Bridge.LastOfType("notify"));
		Assert.Equal("error", toast.GetProperty("level").GetString());
		Assert.Contains("no-such-ref", toast.GetProperty("message").GetString());
		Assert.Null(host.Bridge.LastOfType("pr-changes"));
	}

	[Fact]
	public async Task DiffAgainst_NothingDiffers_ToastsAndRetractsThePriorReview() {
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		File.WriteAllText(path, "hello\nworld\n");
		host.Send("""{"type":"diff-against","ref":"HEAD"}""");
		await Wait.ForAsync(() => host.Bridge.LastOfType("pr-changes"));

		File.WriteAllText(path, "hello\n"); // back to the committed content — nothing differs now
		host.Bridge.Clear();
		host.Send("""{"type":"diff-against","ref":"HEAD"}""");

		var toast = await Wait.ForAsync(() => host.Bridge.LastOfType("notify"));
		Assert.Equal("info", toast.GetProperty("level").GetString());
		Assert.Contains("No changes against 'HEAD'", toast.GetProperty("message").GetString());
		// The prior review is retracted with an empty file list, so the stale walk clears in the page.
		var changes = await Wait.ForAsync(() => host.Bridge.LastOfType("pr-changes"));
		Assert.Empty(changes.GetProperty("files").EnumerateArray());
	}

	[Fact]
	public async Task DiffAgainst_RefAheadOfHead_DiffsFromTheMergeBase_NotTheirSide() {
		await using var host = await TestHost.StartAsync();
		// A branch one commit AHEAD of main: merge-base(topic, HEAD) == HEAD, so with a clean tree there is
		// nothing on THIS side to review — never a reversed diff of the branch's own changes.
		Git(host.RepoRoot, "checkout", "-q", "-b", "topic");
		File.WriteAllText(Path.Combine(host.RepoRoot, "topic.txt"), "theirs\n");
		Git(host.RepoRoot, "add", "-A");
		Git(host.RepoRoot, "-c", "user.email=test@weavie.dev", "-c", "user.name=Weavie Test", "-c", "commit.gpgsign=false", "commit", "-m", "topic work");
		Git(host.RepoRoot, "checkout", "-q", "main");
		host.Bridge.Clear();

		host.Send("""{"type":"diff-against","ref":"topic"}""");

		var toast = await Wait.ForAsync(() => host.Bridge.LastOfType("notify"));
		Assert.Contains("No changes against 'topic'", toast.GetProperty("message").GetString());
	}

	private static void Git(string cwd, params string[] args) {
		var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
		foreach (string arg in args) {
			psi.ArgumentList.Add(arg);
		}

		using var process = Process.Start(psi) ?? throw new InvalidOperationException("git failed to start");
		process.WaitForExit();
		if (process.ExitCode != 0) {
			throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
		}
	}
}
