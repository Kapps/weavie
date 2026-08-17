using System.Diagnostics;
using Weavie.Core.Git;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Drives the blame surface against a real repository, because its value is entirely in agreeing with
/// <c>git</c>: that a line's blame names the commit that wrote it, that the original line number it reports
/// anchors the right hunk, and that a line's history reaches back past its latest rewrite.
/// </summary>
public sealed class GitBlameIntegrationTests : IDisposable {
	private readonly string _repo;

	public GitBlameIntegrationTests() {
		_repo = Path.Combine(Path.GetTempPath(), "weavie-git-blame-" + Guid.NewGuid().ToString("n"));
		Directory.CreateDirectory(_repo);
		RunGit("init", "--quiet", "-b", "main");
	}

	[Fact]
	public async Task Blame_AttributesEachLineToTheCommitThatWroteIt() {
		Write("notes.md", "alpha\nbravo\ncharlie\n");
		string first = Commit("first");
		Write("notes.md", "alpha\nBRAVO\ncharlie\n");
		string second = Commit("second");

		var blame = await new GitService().BlameFileAsync(_repo, "notes.md");

		Assert.Equal(3, blame.LineCommits.Count);
		Assert.Equal(first, ShaOf(blame, 1));
		Assert.Equal(second, ShaOf(blame, 2));
		Assert.Equal(first, ShaOf(blame, 3));
		Assert.Equal("second", blame.Commits[blame.LineCommits[1]].Summary);
		Assert.All(blame.Commits, commit => Assert.False(commit.Uncommitted));
	}

	[Fact]
	public async Task Blame_ReportsAWorkingTreeLineAsUncommitted() {
		Write("notes.md", "alpha\n");
		Commit("first");
		Write("notes.md", "alpha\ntyped just now\n");

		var blame = await new GitService().BlameFileAsync(_repo, "notes.md");

		Assert.False(blame.Commits[blame.LineCommits[0]].Uncommitted);
		Assert.True(blame.Commits[blame.LineCommits[1]].Uncommitted);
	}

	[Fact]
	public async Task CommitHunk_FindsTheChangeAtTheLineBlameReported() {
		Write("notes.md", "one\ntwo\nthree\nfour\nfive\n");
		Commit("first");
		Write("notes.md", "one\ntwo\nTHREE\nfour\nfive\n");
		string second = Commit("second");
		var git = new GitService();
		var blame = await git.BlameFileAsync(_repo, "notes.md");

		// Blame gives the line's number inside the commit that wrote it; that is what selects the hunk.
		var hunk = await git.CommitHunkAsync(_repo, second, "notes.md", blame.LineOriginalLines[2]);

		Assert.NotNull(hunk);
		Assert.Contains("+THREE", hunk.Lines);
		Assert.Contains("-three", hunk.Lines);
		// Context lines come along, so the change reads against the code around it.
		Assert.Contains(" two", hunk.Lines);
	}

	[Fact]
	public async Task CommitHunk_ReturnsNullWhenTheCommitDidNotTouchThatLine() {
		Write("notes.md", string.Concat(Enumerable.Range(1, 40).Select(n => $"line {n}\n")));
		Commit("first");
		Write("notes.md", string.Concat(Enumerable.Range(1, 40).Select(n => n == 40 ? "changed\n" : $"line {n}\n")));
		string second = Commit("second");

		Assert.Null(await new GitService().CommitHunkAsync(_repo, second, "notes.md", 1));
	}

	[Fact]
	public async Task CommitHunk_RejectsAnythingThatIsNotAFullSha() =>
		await Assert.ThrowsAsync<GitException>(
			() => new GitService().CommitHunkAsync(_repo, "HEAD", "notes.md", 1));

	[Fact]
	public async Task LogLines_ListsEveryCommitThatChangedTheLineWithItsNumberThere() {
		Write("notes.md", "alpha\nbravo\n");
		string first = Commit("first");
		Write("notes.md", "alpha\nBRAVO\n");
		string second = Commit("second");
		// A commit that only shifts the line down must not be reported as having changed it.
		Write("notes.md", "header\nalpha\nBRAVO\n");
		string prepend = Commit("prepend");

		var git = new GitService();
		var commits = await git.LogLinesAsync(_repo, prepend, "notes.md", 3, 3, 10);

		Assert.Equal([second, first], commits.Select(c => c.Commit.Sha));
		// The line sat at 2 in both, before the prepend pushed it to 3 — the anchor for each one's hunk.
		Assert.Equal([2, 2], commits.Select(c => c.Line));
		var hunk = await git.CommitHunkAsync(_repo, second, "notes.md", commits[0].Line);
		Assert.Contains("+BRAVO", Assert.IsType<GitDiffHunk>(hunk).Lines);
	}

	[Fact]
	public async Task LogLines_AnchoredAtTheBlamedCommitAnswersAboutTheBlamedLine() {
		// The defect this pins: blame numbers lines against the WORKING TREE, `git log -L` against whatever
		// commit it starts from. Walking from HEAD with a working-tree line number reports a different line
		// once the file has uncommitted line-count changes above it — and dies outright when the tree is longer.
		Write("notes.md", "alpha\nbravo\ncharlie\n");
		Commit("first");
		Write("notes.md", "alpha\nBRAVO\ncharlie\n");
		string second = Commit("second");
		// Two uncommitted lines above it: "BRAVO" is line 4 in the buffer but line 2 in the commit that wrote it.
		Write("notes.md", "new\nlines\nalpha\nBRAVO\ncharlie\n");

		var git = new GitService();
		var blame = await git.BlameFileAsync(_repo, "notes.md");
		int bufferLine = 4;
		var blamed = blame.Commits[blame.LineCommits[bufferLine - 1]];
		int originalLine = blame.LineOriginalLines[bufferLine - 1];
		Assert.Equal(second, blamed.Sha);
		Assert.Equal(2, originalLine);

		var commits = await git.LogLinesAsync(_repo, blamed.Sha, "notes.md", originalLine, originalLine, 10);

		Assert.Equal(["second", "first"], commits.Select(c => c.Commit.Summary));

		// The buffer's own numbering, walked from HEAD, is the wrong question: the tree is longer than HEAD,
		// so Git refuses it rather than quietly answering about some other line.
		string head = await git.GetHeadCommitAsync(_repo);
		await Assert.ThrowsAsync<GitException>(
			() => git.LogLinesAsync(_repo, head, "notes.md", bufferLine, bufferLine, 10));
	}

	[Fact]
	public async Task LogLines_RejectsAnythingThatIsNotAFullSha() {
		Write("notes.md", "alpha\n");
		Commit("first");

		await Assert.ThrowsAsync<GitException>(
			() => new GitService().LogLinesAsync(_repo, "HEAD", "notes.md", 1, 1, 10));
	}

	[Fact]
	public async Task LogFile_FollowsTheFileAcrossARename() {
		Write("notes.md", "alpha\n");
		Commit("first");
		RunGit("mv", "notes.md", "renamed.md");
		Commit("rename");

		var commits = await new GitService().LogFileAsync(_repo, "renamed.md", 10);

		Assert.Equal(["rename", "first"], commits.Select(c => c.Summary));
	}

	[Fact]
	public async Task LogFile_RespectsTheLimit() {
		Write("notes.md", "alpha\n");
		Commit("first");
		Write("notes.md", "beta\n");
		Commit("second");
		Write("notes.md", "gamma\n");
		Commit("third");

		var commits = await new GitService().LogFileAsync(_repo, "notes.md", 2);

		Assert.Equal(["third", "second"], commits.Select(c => c.Summary));
	}

	[Fact]
	public async Task CommitHunk_ReadsAMergeAgainstItsFirstParent() {
		Write("notes.md", "base\n");
		Commit("base");
		RunGit("checkout", "--quiet", "-b", "side");
		Write("side.md", "from the side\n");
		Commit("side change");
		RunGit("checkout", "--quiet", "main");
		RunGit("-c", "user.email=test@weavie.dev", "-c", "user.name=Weavie Test",
			"-c", "commit.gpgsign=false", "merge", "--quiet", "--no-ff", "-m", "merge side", "side");
		string merge = Head();

		// git show prints nothing for a merge by default; --first-parent makes it the diff the merge introduced.
		var hunk = await new GitService().CommitHunkAsync(_repo, merge, "side.md", 1);

		Assert.Contains("+from the side", Assert.IsType<GitDiffHunk>(hunk).Lines);
	}

	private static string? ShaOf(GitBlame blame, int line) => blame.Commits[blame.LineCommits[line - 1]].Sha;

	private void Write(string name, string content) =>
		File.WriteAllText(Path.Combine(_repo, name), content);

	private string Commit(string message) {
		RunGit("add", "-A");
		RunGit(
			"-c", "user.email=test@weavie.dev",
			"-c", "user.name=Weavie Test",
			"-c", "commit.gpgsign=false",
			"commit", "--quiet", "-m", message);
		return Head();
	}

	private string Head() => RunGit("rev-parse", "HEAD").Trim();

	private string RunGit(params string[] args) {
		var info = new ProcessStartInfo("git") {
			WorkingDirectory = _repo,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};
		foreach (string arg in args) {
			info.ArgumentList.Add(arg);
		}

		using var process = Process.Start(info) ?? throw new InvalidOperationException("git failed to start");
		string output = process.StandardOutput.ReadToEnd();
		string error = process.StandardError.ReadToEnd();
		process.WaitForExit();
		return process.ExitCode == 0
			? output
			: throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error.Trim()}");
	}

	public void Dispose() {
		try {
			Directory.Delete(_repo, recursive: true);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			// Best-effort temp cleanup; a lingering handle may outlive the test briefly on Windows.
		}
	}
}
