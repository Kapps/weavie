using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// The "diff against &lt;ref&gt;" flow (web <c>diff-against</c>): resolve the ref, diff the working tree from
/// its merge-base with HEAD, and SEED the change tracker so the diff reviews through the same accept/reject engine
/// as a turn — feeding the navigator over <c>turn-changes</c> / <c>turn-diff</c>, with keep/revert acting on disk.
/// A toast answers an unknown ref or nothing differing. See docs/specs/diff-against.md.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class DiffAgainstTests {
	[Fact]
	public async Task DiffAgainstHead_PushesUncommittedChanges_AndServesTheFileDiff() {
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		File.WriteAllText(path, "hello\nworld\n"); // committed content is "hello\n"

		host.Send("""{"type":"diff-against","ref":"HEAD"}""");

		var changes = await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));
		Assert.Equal("vs HEAD", changes.GetProperty("label").GetString());
		var files = changes.GetProperty("files").EnumerateArray().ToList();
		var file = Assert.Single(files);
		Assert.Equal(path, file.GetProperty("path").GetString());
		Assert.Equal(1, file.GetProperty("added").GetInt32());

		// The per-file diff pairs the merge-base content (baseline) with the worktree file (current), reviewed
		// through the applied engine (acceptedBaseline == baseline until a hunk is kept).
		host.Send($$"""{"type":"get-turn-diff","path":{{JsonSerializer.Serialize(path)}}}""");
		var diff = await Wait.ForAsync(() => host.Bridge.LastOfType("turn-diff"));
		Assert.Equal("hello\n", diff.GetProperty("baseline").GetString());
		Assert.Equal("hello\nworld\n", diff.GetProperty("current").GetString());
		Assert.Equal("hello\n", diff.GetProperty("acceptedBaseline").GetString());
	}

	[Fact]
	public async Task DiffAgainstHead_SeedsDurableReviewBeforeProjectionMount() {
		await using var host = await TestHost.StartAsync();
		host.AutoMountEditorProjection = false;
		// Re-offer the current session and hold the page in its Monaco rebind window.
		host.Send($$"""{"type":"acquire-editor","pageId":"{{TestHost.TestPageId}}"}""");
		host.Bridge.Clear();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		File.WriteAllText(path, "hello\nworld\n");

		host.Send("""{"type":"diff-against","ref":"HEAD"}""");
		await Wait.ForAsync(() => host.Core.ActiveSessionForTest()!.Changes.TurnChanges().Count == 1 ? 1 : (int?)null);
		Assert.Empty(host.Bridge.PostedOfType("turn-changes"));
		host.Core.ActiveSessionForTest()!.EditorChannel.ShowDiff(
			"held-diff", """{"type":"show-diff","id":"held-diff"}""");

		host.MountEditorProjection();
		var changes = await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));
		Assert.Equal("vs HEAD", changes.GetProperty("label").GetString());
		Assert.Equal(path, Assert.Single(changes.GetProperty("files").EnumerateArray()).GetProperty("path").GetString());
		int resetIndex = host.Bridge.Posted.ToList().FindIndex(message => message.Contains("\"type\":\"turn-reset\"", StringComparison.Ordinal));
		int showIndex = host.Bridge.Posted.ToList().FindIndex(message => message.Contains("\"type\":\"show-diff\"", StringComparison.Ordinal));
		Assert.InRange(resetIndex, 0, showIndex - 1);
	}

	[Fact]
	public async Task DiffAgainstHead_RevertFile_RestoresRefContentOnDisk() {
		// Diff Against is no longer read-only: a Reject writes the ref (baseline) back over the worktree file — an
		// uncommitted backout — through the same tracker a turn revert uses.
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		File.WriteAllText(path, "hello\nworld\n"); // committed content is "hello\n"

		host.Send("""{"type":"diff-against","ref":"HEAD"}""");
		await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));
		host.Bridge.Clear();

		host.Send($$"""{"type":"revert-file","path":{{JsonSerializer.Serialize(path)}}}""");

		// The reverted file leaves the walk (its turn-changes carries no files), pushed AFTER the disk write, so
		// observing it means the backout already landed on disk.
		var changes = await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));
		Assert.Empty(changes.GetProperty("files").EnumerateArray());
		Assert.Equal("hello\n", File.ReadAllText(path)); // backed out to the ref on disk
	}

	[Fact]
	public async Task DiffAgainstParent_ShowsTheLastCommitPlusUncommitted() {
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "readme.txt"), "hello\nagain\n");
			Commit(repo, "second");
		});
		File.WriteAllText(Path.Combine(host.RepoRoot, "extra.txt"), "new\n");

		host.Send("""{"type":"diff-against","ref":"HEAD^"}""");

		var changes = await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));
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
		var changes = await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));
		var file = Assert.Single(changes.GetProperty("files").EnumerateArray());
		Assert.Equal("brand-new.txt", file.GetProperty("name").GetString());
		// Counted in model lines (the trailing newline is a 3rd, empty line), exactly as turn review counts a
		// created file — the unification shares one count, not git's numstat.
		Assert.Equal(3, file.GetProperty("added").GetInt32());
	}

	[Fact]
	public async Task RevertingAChangedEmptyTrackedFile_PreservesItsExistence() {
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "empty.txt"), string.Empty);
			Commit(repo, "track empty file");
		});
		string path = Path.Combine(host.RepoRoot, "empty.txt");
		File.WriteAllText(path, "added\n");

		host.Send("""{"type":"diff-against","ref":"HEAD"}""");
		await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));
		host.Send(JsonSerializer.Serialize(new { type = "revert-file", path }));

		Assert.True(File.Exists(path));
		Assert.Equal(string.Empty, File.ReadAllText(path));
	}

	[Fact]
	public async Task GetTurnDiff_ForAnUntrackedPath_IsDropped() {
		await using var host = await TestHost.StartAsync();
		File.WriteAllText(Path.Combine(host.RepoRoot, "readme.txt"), "hello\nworld\n");
		host.Send("""{"type":"diff-against","ref":"HEAD"}""");
		await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));

		// The seeded tracker only knows the review's files, so a diff request for a path it never seeded (a
		// switch-race leftover, or a foreign file) resolves nothing and is dropped, never rendered.
		string foreign = Path.Combine(Path.GetTempPath(), "weavie-foreign-" + Guid.NewGuid().ToString("n") + ".txt");
		File.WriteAllText(foreign, "elsewhere\n");
		try {
			host.Bridge.Clear();
			host.Send($$"""{"type":"get-turn-diff","path":{{JsonSerializer.Serialize(foreign)}}}""");

			await Task.Delay(300);
			Assert.Null(host.Bridge.LastOfType("turn-diff"));
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
		Assert.Equal("warn", toast.GetProperty("level").GetString());
		Assert.Contains("no-such-ref", toast.GetProperty("message").GetString());
		Assert.Null(host.Bridge.LastOfType("turn-changes"));
	}

	[Fact]
	public async Task DiffAgainst_NothingDiffers_ToastsAndRetractsThePriorReview() {
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		File.WriteAllText(path, "hello\nworld\n");
		host.Send("""{"type":"diff-against","ref":"HEAD"}""");
		await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));

		File.WriteAllText(path, "hello\n"); // back to the committed content — nothing differs now
		host.Bridge.Clear();
		host.Send("""{"type":"diff-against","ref":"HEAD"}""");

		var toast = await Wait.ForAsync(() => host.Bridge.LastOfType("notify"));
		Assert.Equal("info", toast.GetProperty("level").GetString());
		Assert.Contains("No changes against 'HEAD'", toast.GetProperty("message").GetString());
		// The prior review is retracted: the tracker's board is committed and an empty review set is pushed, so
		// the stale walk clears in the page.
		var changes = await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));
		Assert.Empty(changes.GetProperty("files").EnumerateArray());
	}

	[Fact]
	public async Task DiffAgainst_NothingDiffers_DoesNotCommitAnOrdinaryPendingReview() {
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		var tracker = host.Core.ActiveSessionForTest()!.Changes;
		tracker.CaptureBaseline(path);
		File.WriteAllText(path, "hello\nworld\n");
		tracker.RecordChange(path);
		Commit(host.RepoRoot, "commit pending agent edit");
		host.Bridge.Clear();

		host.Send("""{"type":"diff-against","ref":"HEAD"}""");

		var toast = await Wait.ForAsync(() => host.Bridge.LastOfType("notify"));
		Assert.Contains("No changes against 'HEAD'", toast.GetProperty("message").GetString());
		var pending = Assert.IsType<Weavie.Core.Changes.FileChange>(tracker.GetTurn(path));
		Assert.Equal("hello\n", pending.BaselineText);
		Assert.Equal("hello\nworld\n", pending.CurrentText);
	}

	[Fact]
	public async Task DiffAgainst_RefAheadOfHead_DiffsFromTheMergeBase_NotTheirSide() {
		// A branch one commit AHEAD of main: merge-base(topic, HEAD) == HEAD, so with a clean tree there is
		// nothing on THIS side to review — never a reversed diff of the branch's own changes.
		await using var host = await TestHost.StartAsync(repo => {
			Git(repo, "checkout", "-q", "-b", "topic");
			File.WriteAllText(Path.Combine(repo, "topic.txt"), "theirs\n");
			Commit(repo, "topic work");
			Git(repo, "checkout", "-q", "main");
		});
		host.Bridge.Clear();

		host.Send("""{"type":"diff-against","ref":"topic"}""");

		var toast = await Wait.ForAsync(() => host.Bridge.LastOfType("notify"));
		Assert.Contains("No changes against 'topic'", toast.GetProperty("message").GetString());
	}

	private static void Commit(string cwd, string message) {
		Git(cwd, "add", "-A");
		Git(cwd, "-c", "user.email=test@weavie.dev", "-c", "user.name=Weavie Test", "-c", "commit.gpgsign=false", "commit", "-m", message);
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
