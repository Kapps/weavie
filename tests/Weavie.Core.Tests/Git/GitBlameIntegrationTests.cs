using Weavie.Core.Git;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Drives the blame surface against a real repository, because its value is entirely in agreeing with
/// <c>git</c>: that a line's blame names the commit that wrote it, that the original line number it reports
/// anchors the right hunk, and that a line's history reaches back past its latest rewrite.
/// </summary>
public sealed class GitBlameIntegrationTests : IDisposable {
	private readonly TempGitRepo _repo = new("weavie-git-blame");

	[Fact]
	public async Task Blame_AttributesEachLineToTheCommitThatWroteIt() {
		_repo.Write("notes.md", "alpha\nbravo\ncharlie\n");
		string first = _repo.Commit("first");
		_repo.Write("notes.md", "alpha\nBRAVO\ncharlie\n");
		string second = _repo.Commit("second");

		var blame = await new GitService().BlameFileAsync(_repo.Path, "notes.md");

		Assert.Equal(3, blame.LineCommits.Count);
		Assert.Equal(first, ShaOf(blame, 1));
		Assert.Equal(second, ShaOf(blame, 2));
		Assert.Equal(first, ShaOf(blame, 3));
		Assert.Equal("second", blame.Commits[blame.LineCommits[1]].Summary);
		Assert.All(blame.Commits, commit => Assert.False(commit.Uncommitted));
	}

	[Fact]
	public async Task Blame_ReportsAWorkingTreeLineAsUncommitted() {
		_repo.Write("notes.md", "alpha\n");
		_repo.Commit("first");
		_repo.Write("notes.md", "alpha\ntyped just now\n");

		var blame = await new GitService().BlameFileAsync(_repo.Path, "notes.md");

		Assert.False(blame.Commits[blame.LineCommits[0]].Uncommitted);
		Assert.True(blame.Commits[blame.LineCommits[1]].Uncommitted);
	}

	[Fact]
	public async Task CommitHunk_FindsTheChangeAtTheLineBlameReported() {
		_repo.Write("notes.md", "one\ntwo\nthree\nfour\nfive\n");
		_repo.Commit("first");
		_repo.Write("notes.md", "one\ntwo\nTHREE\nfour\nfive\n");
		string second = _repo.Commit("second");
		var git = new GitService();
		var blame = await git.BlameFileAsync(_repo.Path, "notes.md");

		// Blame gives the line's number inside the commit that wrote it; that is what selects the hunk.
		var hunk = await git.CommitHunkAsync(_repo.Path, second, "notes.md", blame.LineOriginalLines[2]);

		Assert.NotNull(hunk);
		Assert.Contains("+THREE", hunk.Lines);
		Assert.Contains("-three", hunk.Lines);
		// Context lines come along, so the change reads against the code around it.
		Assert.Contains(" two", hunk.Lines);
	}

	[Fact]
	public async Task CommitHunk_ReturnsNullWhenTheCommitDidNotTouchThatLine() {
		_repo.Write("notes.md", string.Concat(Enumerable.Range(1, 40).Select(n => $"line {n}\n")));
		_repo.Commit("first");
		_repo.Write("notes.md", string.Concat(Enumerable.Range(1, 40).Select(n => n == 40 ? "changed\n" : $"line {n}\n")));
		string second = _repo.Commit("second");

		Assert.Null(await new GitService().CommitHunkAsync(_repo.Path, second, "notes.md", 1));
	}

	[Fact]
	public async Task CommitHunk_RejectsAnythingThatIsNotAFullSha() =>
		await Assert.ThrowsAsync<GitException>(
			() => new GitService().CommitHunkAsync(_repo.Path, "HEAD", "notes.md", 1));

	[Fact]
	public async Task LogLines_ListsEveryCommitThatChangedTheLineWithItsNumberThere() {
		_repo.Write("notes.md", "alpha\nbravo\n");
		string first = _repo.Commit("first");
		_repo.Write("notes.md", "alpha\nBRAVO\n");
		string second = _repo.Commit("second");
		// A commit that only shifts the line down must not be reported as having changed it.
		_repo.Write("notes.md", "header\nalpha\nBRAVO\n");
		string prepend = _repo.Commit("prepend");

		var git = new GitService();
		var commits = await git.LogLinesAsync(_repo.Path, prepend, "notes.md", 3, 3, 10);

		Assert.Equal([second, first], commits.Select(c => c.Commit.Sha));
		// The line sat at 2 in both, before the prepend pushed it to 3 — the anchor for each one's hunk.
		Assert.Equal([2, 2], commits.Select(c => c.Line));
		var hunk = await git.CommitHunkAsync(_repo.Path, second, "notes.md", commits[0].Line);
		Assert.Contains("+BRAVO", Assert.IsType<GitDiffHunk>(hunk).Lines);
	}

	[Fact]
	public async Task LogLines_AnchoredAtTheBlamedCommitAnswersAboutTheBlamedLine() {
		// The defect this pins: blame numbers lines against the WORKING TREE, `git log -L` against whatever
		// commit it starts from. Walking from HEAD with a working-tree line number reports a different line
		// once the file has uncommitted line-count changes above it — and dies outright when the tree is longer.
		_repo.Write("notes.md", "alpha\nbravo\ncharlie\n");
		_repo.Commit("first");
		_repo.Write("notes.md", "alpha\nBRAVO\ncharlie\n");
		string second = _repo.Commit("second");
		// Two uncommitted lines above it: "BRAVO" is line 4 in the buffer but line 2 in the commit that wrote it.
		_repo.Write("notes.md", "new\nlines\nalpha\nBRAVO\ncharlie\n");

		var git = new GitService();
		var blame = await git.BlameFileAsync(_repo.Path, "notes.md");
		int bufferLine = 4;
		var blamed = blame.Commits[blame.LineCommits[bufferLine - 1]];
		int originalLine = blame.LineOriginalLines[bufferLine - 1];
		Assert.Equal(second, blamed.Sha);
		Assert.Equal(2, originalLine);

		var commits = await git.LogLinesAsync(_repo.Path, blamed.Sha, "notes.md", originalLine, originalLine, 10);

		Assert.Equal(["second", "first"], commits.Select(c => c.Commit.Summary));

		// The buffer's own numbering, walked from HEAD, is the wrong question: the tree is longer than HEAD,
		// so Git refuses it rather than quietly answering about some other line.
		string head = await git.GetHeadCommitAsync(_repo.Path);
		await Assert.ThrowsAsync<GitException>(
			() => git.LogLinesAsync(_repo.Path, head, "notes.md", bufferLine, bufferLine, 10));
	}

	[Fact]
	public async Task LogLines_RejectsAnythingThatIsNotAFullSha() {
		_repo.Write("notes.md", "alpha\n");
		_repo.Commit("first");

		await Assert.ThrowsAsync<GitException>(
			() => new GitService().LogLinesAsync(_repo.Path, "HEAD", "notes.md", 1, 1, 10));
	}

	[Fact]
	public async Task LogFile_FollowsTheFileAcrossARename() {
		_repo.Write("notes.md", "alpha\n");
		_repo.Commit("first");
		_repo.Git("mv", "notes.md", "renamed.md");
		_repo.Commit("rename");

		var commits = await new GitService().LogFileAsync(_repo.Path, "renamed.md", 10);

		Assert.Equal(["rename", "first"], commits.Select(c => c.Summary));
	}

	[Fact]
	public async Task LogFile_RespectsTheLimit() {
		_repo.Write("notes.md", "alpha\n");
		_repo.Commit("first");
		_repo.Write("notes.md", "beta\n");
		_repo.Commit("second");
		_repo.Write("notes.md", "gamma\n");
		_repo.Commit("third");

		var commits = await new GitService().LogFileAsync(_repo.Path, "notes.md", 2);

		Assert.Equal(["third", "second"], commits.Select(c => c.Summary));
	}

	[Fact]
	public async Task CommitHunk_ReadsAMergeAgainstItsFirstParent() {
		_repo.Write("notes.md", "base\n");
		_repo.Commit("base");
		_repo.Git("checkout", "--quiet", "-b", "side");
		_repo.Write("side.md", "from the side\n");
		_repo.Commit("side change");
		_repo.Git("checkout", "--quiet", "main");
		_repo.Git("-c", "user.email=test@weavie.dev", "-c", "user.name=Weavie Test",
			"-c", "commit.gpgsign=false", "merge", "--quiet", "--no-ff", "-m", "merge side", "side");
		string merge = _repo.Head();

		// git show prints nothing for a merge by default; --first-parent makes it the diff the merge introduced.
		var hunk = await new GitService().CommitHunkAsync(_repo.Path, merge, "side.md", 1);

		Assert.Contains("+from the side", Assert.IsType<GitDiffHunk>(hunk).Lines);
	}

	private static string? ShaOf(GitBlame blame, int line) => blame.Commits[blame.LineCommits[line - 1]].Sha;

	public void Dispose() => _repo.Dispose();
}
