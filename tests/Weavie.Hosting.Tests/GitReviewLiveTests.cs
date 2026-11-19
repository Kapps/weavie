using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// An open git review stays git's answer as the worktree changes under it: known edits join it one path at a time,
/// anything else re-lists it, and a pull never shows what it brought in. See docs/specs/review-state.md.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class GitReviewLiveTests {
	[Fact]
	public async Task AnAgentEdit_JoinsAgainstTheBase() {
		await using var host = await OpenAsync();
		string path = Path.Combine(host.RepoRoot, "notes.txt");
		var changes = host.SelectedSession.Changes;

		changes.CaptureBaseline(path);
		File.WriteAllText(path, "notes\nagent\n");
		changes.RecordChange(path);

		await WaitForRowsAsync(host, "notes.txt", "readme.txt");
		Assert.Equal("notes\n", changes.GetTurn(path)!.AcceptedBaselineText);
	}

	[Fact]
	public async Task AnEditorSave_JoinsAndASaveBackToTheBase_Leaves() {
		await using var host = await OpenAsync();
		string path = Path.Combine(host.RepoRoot, "notes.txt");

		await SaveAsync(host, path, "notes\nmine\n");
		await WaitForRowsAsync(host, "notes.txt", "readme.txt");
		await SaveAsync(host, path, "notes\n");

		await WaitForRowsAsync(host, "readme.txt");
	}

	[Fact]
	public async Task AShellEdit_IsListedOnceTheWatcherReportsIt() {
		await using var host = await OpenAsync();

		File.WriteAllText(Path.Combine(host.RepoRoot, "notes.txt"), "notes\nfrom a shell\n");

		await WaitForRowsAsync(host, "notes.txt", "readme.txt");
	}

	[Fact]
	public async Task APull_NeverListsWhatItBroughtIn() {
		await using var host = await OpenAsync(repo => {
			TempGitRepo.Run(repo, "switch", "-q", "-c", "upstream");
			for (int i = 0; i < 40; i++) File.WriteAllText(Path.Combine(repo, $"pulled{i}.txt"), $"pulled {i}\n");
			TempGitRepo.Run(repo, "add", "-A");
			TempGitRepo.Run(repo, "commit", "-q", "-m", "upstream work");
			TempGitRepo.Run(repo, "switch", "-q", "main");
		});

		TempGitRepo.Run(host.RepoRoot, "merge", "-q", "--ff-only", "upstream");
		string head = TempGitRepo.Run(host.RepoRoot, "rev-parse", "HEAD").Trim();

		await Wait.UntilAsync(() => host.SelectedSession.Changes.Review?.MergeBase == head);
		await WaitForRowsAsync(host, "readme.txt");
		Assert.All(host.Bridge.PostedEvents(host.SelectedSession.Address, "review", "changes"),
			changes => Assert.DoesNotContain(Names(changes), name => name.StartsWith("pulled", StringComparison.Ordinal)));
	}

	[Fact]
	public async Task Reopening_KeepsWhatWasKept_ButClosingStartsOver() {
		await using var host = await OpenAsync();
		var session = host.SelectedSession;
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		session.Changes.KeepFile(path);

		await DiffAgainstHeadAsync(host);
		Assert.Equal("hello\nworld\n", session.Changes.GetTurn(path)!.BaselineText);

		host.SessionEvent(session, "review", "close", new { });
		await Wait.UntilAsync(() => session.Changes.Review is null);
		await DiffAgainstHeadAsync(host);
		Assert.Equal("hello\n", session.Changes.GetTurn(path)!.BaselineText);
	}

	[Fact]
	public async Task KeepAll_LeavesTheReviewOpenAndUndoable() {
		await using var host = await OpenAsync();
		var session = host.SelectedSession;

		host.SessionEvent(session, "review", "accept", new { });

		await Wait.UntilAsync(() => session.Changes.CanUndoKeep);
		Assert.Equal("vs HEAD", session.Changes.Review?.Label);
		Assert.Equal(["readme.txt"], Rows(host));
	}

	// A committed notes.txt beside the host's readme.txt, and a review of the uncommitted readme change.
	private static async Task<TestHost> OpenAsync(Action<string> prepare) {
		var host = await TestHost.StartAsync(repo => {
			File.WriteAllText(Path.Combine(repo, "notes.txt"), "notes\n");
			TempGitRepo.Run(repo, "add", "-A");
			TempGitRepo.Run(repo, "commit", "-q", "-m", "notes");
			prepare(repo);
		});
		File.WriteAllText(Path.Combine(host.RepoRoot, "readme.txt"), "hello\nworld\n");
		await DiffAgainstHeadAsync(host);
		await WaitForRowsAsync(host, "readme.txt");
		return host;
	}

	private static Task<TestHost> OpenAsync() => OpenAsync(_ => { });

	private static Task<JsonElement> DiffAgainstHeadAsync(TestHost host) =>
		host.SessionRequestAsync<JsonElement>(host.SelectedSession, "review", "diffAgainst", new { reference = "HEAD" });

	private static Task<JsonElement> SaveAsync(TestHost host, string path, string content) =>
		host.SessionRequestAsync<JsonElement>(host.SelectedSession, "files", "write", new { path, content });

	private static Task WaitForRowsAsync(TestHost host, params string[] names) =>
		Wait.UntilAsync(() => Rows(host).SequenceEqual(names));

	private static string[] Rows(TestHost host) =>
		host.Bridge.LastEvent(host.SelectedSession.Address, "review", "changes") is { } changes ? [.. Names(changes).Order()] : [];

	private static IEnumerable<string> Names(JsonElement changes) =>
		changes.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("name").GetString()!);
}
