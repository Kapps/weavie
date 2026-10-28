using System.Text.Json;
using Weavie.Core.Changes;
using Weavie.Core.Sessions;
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
		var session = host.SelectedSession;

		host.SessionEvent(session, "review", "diffAgainst", new { reference = "HEAD" });

		var changes = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(session.Address, "review", "changes"));
		Assert.Equal("vs HEAD", changes.GetProperty("label").GetString());
		var files = changes.GetProperty("files").EnumerateArray().ToList();
		var file = Assert.Single(files);
		Assert.Equal(path, file.GetProperty("path").GetString());
		Assert.Equal(1, file.GetProperty("added").GetInt32());

		// The per-file diff names the merge-base content (baseline) and the worktree file (current) by revision,
		// reviewed through the applied engine (accepted anchor == baseline until a hunk is kept).
		host.SessionEvent(session, "review", "showFile", new { path });
		var diff = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(session.Address, "review", "diff"));
		string? baselineRevision = diff.GetProperty("baselineRevision").GetString();
		Assert.Equal(TextRevision.Of("hello\n"), baselineRevision);
		Assert.Equal(TextRevision.Of("hello\nworld\n"), diff.GetProperty("revision").GetString());
		Assert.Equal(baselineRevision, diff.GetProperty("acceptedBaselineRevision").GetString());

		var baseline = await host.SessionRequestAsync<JsonElement>(
			session, "review", "text", new { path, revision = baselineRevision });
		Assert.Equal("hello\n", baseline.GetProperty("text").GetString());
		var superseded = await host.SessionRequestAsync<JsonElement>(
			session, "review", "text", new { path, revision = TextRevision.Of("an earlier baseline") });
		Assert.Equal(JsonValueKind.Null, superseded.GetProperty("text").ValueKind);
	}

	[Fact]
	public async Task AFailedReviewActionResendsTheReviewState() {
		// The page fetches review texts by revision and waits on the next push for a superseded one, so an action
		// that fails partway must push the review state again rather than leave a file calculating forever.
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		File.WriteAllText(path, "hello\nworld\n");
		var session = host.SelectedSession;
		host.SessionEvent(session, "review", "diffAgainst", new { reference = "HEAD" });
		await Wait.ForAsync(() => host.Bridge.LastEvent(session.Address, "review", "changes"));
		int changesBefore = host.Bridge.PostedEvents(session.Address, "review", "changes").Count;
		int diffsBefore = host.Bridge.PostedEvents(session.Address, "review", "diff").Count;
		File.SetAttributes(path, FileAttributes.ReadOnly);
		try {
			host.SessionEvent(session, "review", "revertFile", new { path });

			await Wait.UntilAsync(() => host.Bridge.PostedEvents(session.Address, "review", "diff").Count > diffsBefore);
			Assert.True(host.Bridge.PostedEvents(session.Address, "review", "changes").Count > changesBefore);
			Assert.Contains(
				host.Bridge.PostedEvents("notifications", "show"),
				toast => toast.GetProperty("message").GetString()!.StartsWith("Couldn't revert readme.txt", StringComparison.Ordinal));
		} finally {
			File.SetAttributes(path, FileAttributes.Normal);
		}
	}

	[Fact]
	public async Task DiffAgainstHead_RevertFile_RestoresRefContentOnDisk() {
		// Diff Against is no longer read-only: a Reject writes the ref (baseline) back over the worktree file — an
		// uncommitted backout — through the same tracker a turn revert uses.
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		File.WriteAllText(path, "hello\nworld\n"); // committed content is "hello\n"
		var session = host.SelectedSession;

		host.SessionEvent(session, "review", "diffAgainst", new { reference = "HEAD" });
		await Wait.ForAsync(() => host.Bridge.LastEvent(session.Address, "review", "changes"));
		host.Bridge.Clear();

		host.SessionEvent(session, "review", "revertFile", new { path });

		// The row retains its rejected proposal, but no pending additions or removals.
		var changes = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(session.Address, "review", "changes"));
		var rejected = Assert.Single(changes.GetProperty("files").EnumerateArray());
		Assert.Equal(0, rejected.GetProperty("added").GetInt32());
		Assert.Equal(0, rejected.GetProperty("removed").GetInt32());
		Assert.Equal("hello\n", File.ReadAllText(path)); // backed out to the ref on disk
	}

	[Fact]
	public async Task DiffAgainstParent_ShowsTheLastCommitPlusUncommitted() {
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "readme.txt"), "hello\nagain\n");
			Commit(repo, "second");
		});
		File.WriteAllText(Path.Combine(host.RepoRoot, "extra.txt"), "new\n");
		var session = host.SelectedSession;

		host.SessionEvent(session, "review", "diffAgainst", new { reference = "HEAD^" });

		var changes = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(session.Address, "review", "changes"));
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
		var session = host.SelectedSession;

		host.SessionEvent(session, "review", "diffAgainst", new { reference = "HEAD" });

		// A brand-new file IS an uncommitted change — never "No changes against 'HEAD'."
		var changes = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(session.Address, "review", "changes"));
		var file = Assert.Single(changes.GetProperty("files").EnumerateArray());
		Assert.Equal("brand-new.txt", file.GetProperty("name").GetString());
		// Counted in model lines (the trailing newline is a 3rd, empty line), exactly as turn review counts a
		// created file — the unification shares one count, not git's numstat.
		Assert.Equal(3, file.GetProperty("added").GetInt32());
	}

	[Fact]
	public async Task DiffAgainstHead_DeletedFileIsReviewData_NotAnUnreadableEditorOpen() {
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		File.Delete(path);
		var session = host.SelectedSession;
		host.Bridge.Clear();

		host.SessionEvent(session, "review", "diffAgainst", new { reference = "HEAD" });

		var changes = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(session.Address, "review", "changes"));
		var file = Assert.Single(changes.GetProperty("files").EnumerateArray());
		Assert.Equal(path, file.GetProperty("path").GetString());
		Assert.False(file.GetProperty("currentExists").GetBoolean());
		Assert.Null(host.Bridge.LastEvent(session.Address, "editor", "openFile"));

		host.SessionEvent(session, "review", "showFile", new { path });
		var diff = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(session.Address, "review", "diff"));
		Assert.Equal(TextRevision.Of("hello\n"), diff.GetProperty("baselineRevision").GetString());
		Assert.Equal(JsonValueKind.Null, diff.GetProperty("revision").ValueKind);

		host.Bridge.Clear();
		host.SessionEvent(session, "review", "revertFile", new { path });
		await Wait.UntilAsync(() => File.Exists(path));
		host.Bridge.Clear();

		host.SessionEvent(session, "review", "undo", new { kind = "revert" });
		await Wait.UntilAsync(() => !File.Exists(path));
		Assert.Null(host.Bridge.LastEvent(session.Address, "editor", "openFile"));
	}

	[Fact]
	public async Task DiffAgainstHead_EmptyAddedAndDeletedFilesRemainActionable() {
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "empty-deleted.txt"), string.Empty);
			Commit(repo, "empty baseline");
		});
		string deleted = Path.Combine(host.RepoRoot, "empty-deleted.txt");
		string added = Path.Combine(host.RepoRoot, "empty-added.txt");
		File.Delete(deleted);
		File.WriteAllText(added, string.Empty);
		var session = host.SelectedSession;

		host.SessionEvent(session, "review", "diffAgainst", new { reference = "HEAD" });

		var changes = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(session.Address, "review", "changes"));
		var files = changes.GetProperty("files").EnumerateArray().ToList();
		Assert.Equal(2, files.Count);
		Assert.False(Assert.Single(files, file => file.GetProperty("name").GetString() == "empty-deleted.txt")
			.GetProperty("currentExists").GetBoolean());
		Assert.True(Assert.Single(files, file => file.GetProperty("name").GetString() == "empty-added.txt")
			.GetProperty("currentExists").GetBoolean());

		// An empty file differs from no file only by existence, which a null revision carries on its own.
		host.SessionEvent(session, "review", "showFile", new { path = added });
		var addedDiff = await Wait.ForAsync(() => host.Bridge.PostedEvents(session.Address, "review", "diff")
			.Select(diff => (JsonElement?)diff)
			.LastOrDefault(diff => diff!.Value.GetProperty("path").GetString() == added));
		Assert.Equal(JsonValueKind.Null, addedDiff.GetProperty("baselineRevision").ValueKind);
		Assert.Equal(TextRevision.Of(string.Empty), addedDiff.GetProperty("revision").GetString());

		host.SessionEvent(session, "review", "revertAll", new { });
		await session.FileActivity.DrainAsync(CancellationToken.None);
		Assert.True(File.Exists(deleted));
		Assert.Equal(string.Empty, File.ReadAllText(deleted));
		Assert.False(File.Exists(added));
	}

	[Fact]
	public async Task GetTurnDiff_ForAnUntrackedPath_IsDropped() {
		await using var host = await TestHost.StartAsync();
		File.WriteAllText(Path.Combine(host.RepoRoot, "readme.txt"), "hello\nworld\n");
		var session = host.SelectedSession;
		host.SessionEvent(session, "review", "diffAgainst", new { reference = "HEAD" });
		await Wait.ForAsync(() => host.Bridge.LastEvent(session.Address, "review", "changes"));

		// The seeded tracker only knows the review's files, so a diff request for a path it never seeded (a
		// switch-race leftover, or a foreign file) resolves nothing and is dropped, never rendered.
		using var elsewhere = new TempDirectory("weavie-foreign");
		string foreign = elsewhere.WriteFile("foreign.txt", "elsewhere\n");
		host.Bridge.Clear();
		host.SessionEvent(session, "review", "showFile", new { path = foreign });

		await Task.Delay(300);
		Assert.Null(host.Bridge.LastEvent(session.Address, "review", "diff"));
	}

	[Fact]
	public async Task DiffAgainst_UnknownRef_ToastsAndArmsNothing() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Clear();
		var session = host.SelectedSession;

		host.SessionEvent(session, "review", "diffAgainst", new { reference = "no-such-ref" });

		var toast = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(session.Address, "notifications", "show"));
		Assert.Equal("warn", toast.GetProperty("level").GetString());
		Assert.Contains("no-such-ref", toast.GetProperty("message").GetString());
		Assert.Null(host.Bridge.LastEvent(session.Address, "review", "changes"));
	}

	[Fact]
	public async Task DiffAgainst_NothingDiffers_ToastsAndRetractsThePriorReview() {
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		File.WriteAllText(path, "hello\nworld\n");
		var session = host.SelectedSession;
		host.SessionEvent(session, "review", "diffAgainst", new { reference = "HEAD" });
		await Wait.ForAsync(() => host.Bridge.LastEvent(session.Address, "review", "changes"));

		File.WriteAllText(path, "hello\n"); // back to the committed content — nothing differs now
		host.Bridge.Clear();
		host.SessionEvent(session, "review", "diffAgainst", new { reference = "HEAD" });

		var toast = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(session.Address, "notifications", "show"));
		Assert.Equal("info", toast.GetProperty("level").GetString());
		Assert.Contains("No changes against 'HEAD'", toast.GetProperty("message").GetString());
		// The prior review is retracted: the tracker's board is committed and an empty review set is pushed, so
		// the stale walk clears in the page.
		var changes = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(session.Address, "review", "changes"));
		Assert.Empty(changes.GetProperty("files").EnumerateArray());
	}

	[Fact]
	public async Task DiffAgainst_RefAheadOfHead_DiffsFromTheMergeBase_NotTheirSide() {
		// A branch one commit AHEAD of main: merge-base(topic, HEAD) == HEAD, so with a clean tree there is
		// nothing on THIS side to review — never a reversed diff of the branch's own changes.
		await using var host = await TestHost.StartAsync(repo => {
			TempGitRepo.Run(repo, "checkout", "-q", "-b", "topic");
			File.WriteAllText(Path.Combine(repo, "topic.txt"), "theirs\n");
			Commit(repo, "topic work");
			TempGitRepo.Run(repo, "checkout", "-q", "main");
		});
		host.Bridge.Clear();
		var session = host.SelectedSession;

		host.SessionEvent(session, "review", "diffAgainst", new { reference = "topic" });

		var toast = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(session.Address, "notifications", "show"));
		Assert.Contains("No changes against 'topic'", toast.GetProperty("message").GetString());
	}

	[Fact]
	public async Task AnotherSource_IsRefusedUntilReplaced_ThenSwitchesTheReview() {
		await using var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "readme.txt"), "hello\nagain\n");
			Commit(repo, "second");
		});
		File.WriteAllText(Path.Combine(host.RepoRoot, "extra.txt"), "new\n");
		var session = host.SelectedSession;

		var head = await host.SessionRequestAsync<JsonElement>(session, "review", "diffAgainst", new { reference = "HEAD", replaces = (string?)null });
		Assert.True(head.GetProperty("opened").GetBoolean());

		// A different source isn't opened over the current review without the user's say-so.
		var refused = await host.SessionRequestAsync<JsonElement>(session, "review", "diffAgainst", new { reference = "HEAD^", replaces = (string?)null });
		Assert.False(refused.GetProperty("opened").GetBoolean());
		Assert.Equal("vs HEAD", refused.GetProperty("replaces").GetString());
		Assert.Equal("vs HEAD^", refused.GetProperty("label").GetString());
		Assert.Equal("vs HEAD", host.Bridge.LastEvent(session.Address, "review", "changes")!.Value.GetProperty("label").GetString());

		// A confirm that names a different review than the one open is refused again, naming the open one.
		var stale = await host.SessionRequestAsync<JsonElement>(session, "review", "diffAgainst", new { reference = "HEAD^", replaces = "vs main" });
		Assert.False(stale.GetProperty("opened").GetBoolean());
		Assert.Equal("vs HEAD", stale.GetProperty("replaces").GetString());

		var replaced = await host.SessionRequestAsync<JsonElement>(session, "review", "diffAgainst", new { reference = "HEAD^", replaces = "vs HEAD" });
		Assert.True(replaced.GetProperty("opened").GetBoolean());
		var changes = await Wait.ForAsync(() => host.Bridge.LastEvent(session.Address, "review", "changes") is { } last
			&& last.GetProperty("label").GetString() == "vs HEAD^" ? last : (JsonElement?)null);
		Assert.Contains("readme.txt", changes.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
	}

	[Fact]
	public async Task AnEmptyDiffFromAnotherSource_LeavesTheCurrentReview() {
		await using var host = await TestHost.StartAsync();
		File.WriteAllText(Path.Combine(host.RepoRoot, "readme.txt"), "hello\nworld\n");
		var session = host.SelectedSession;
		await host.SessionRequestAsync<JsonElement>(session, "review", "diffAgainst", new { reference = "HEAD", replaces = (string?)null });
		File.WriteAllText(Path.Combine(host.RepoRoot, "readme.txt"), "hello\n");

		var answer = await host.SessionRequestAsync<JsonElement>(session, "review", "diffAgainst", new { reference = "main", replaces = (string?)null });

		Assert.False(answer.GetProperty("opened").GetBoolean());
		Assert.Equal(JsonValueKind.Null, answer.GetProperty("replaces").ValueKind);
	}

	[Fact]
	public async Task ReviewBranch_OnAStackedBranch_ComparesWithItsParent() {
		await using var host = await TestHost.StartAsync(repo => {
			TempGitRepo.Run(repo, "switch", "-q", "-c", "feature-a");
			File.WriteAllText(Path.Combine(repo, "a.txt"), "parent\n");
			Commit(repo, "feature-a work");
			TempGitRepo.Run(repo, "switch", "-q", "-c", "feature-b", "feature-a");
			File.WriteAllText(Path.Combine(repo, "b.txt"), "own\n");
			Commit(repo, "feature-b work");
		});
		var session = host.SelectedSession;

		var opened = await host.SessionRequestAsync<JsonElement>(session, "review", "diffAgainstBase", new { replaces = (string?)null });

		Assert.Equal("vs feature-a", opened.GetProperty("label").GetString());
		var changes = await Wait.ForAsync(() => host.Bridge.LastEvent(session.Address, "review", "changes"));
		Assert.Equal(["b.txt"], changes.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
	}

	[Fact]
	public async Task ReviewBranch_OnASessionBranchedFromAnotherSession_ComparesWithThatSessionsBranch() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("feature-a")).Ok);
		File.WriteAllText(Path.Combine(host.Session("feature-a").WorkspaceRoot, "a.txt"), "parent\n");
		Commit(host.Session("feature-a").WorkspaceRoot, "feature-a work");
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "feature-b", Base = "source" })).Ok);
		var session = host.Session("feature-b");
		File.WriteAllText(Path.Combine(session.WorkspaceRoot, "b.txt"), "own\n");

		var opened = await host.SessionRequestAsync<JsonElement>(session, "review", "diffAgainstBase", new { replaces = (string?)null });

		Assert.Equal("vs feature-a", opened.GetProperty("label").GetString());
		var changes = await Wait.ForAsync(() => host.Bridge.LastEvent(session.Address, "review", "changes"));
		Assert.Equal(["b.txt"], changes.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
	}

	private static void Commit(string cwd, string message) {
		TempGitRepo.Run(cwd, "add", "-A");
		TempGitRepo.Run(cwd, "commit", "-m", message);
	}
}
