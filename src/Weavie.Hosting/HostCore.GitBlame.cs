using Weavie.Core.Git;

namespace Weavie.Hosting;

// Answers the editor's blame surface for one session's worktree: who last changed each line, the hunk a blamed
// line came from, the other commits behind a line or a file, and the forge links for a commit. Every request
// names a file, so each resolves its path against the OWNING session's worktree — never the window's.
public sealed partial class HostCore {
	// A line or file rarely needs more history than this to answer "what else touched it"; the web says so when
	// the list is cut rather than presenting a truncated history as the whole one.
	private const int BlameHistoryLimit = 25;

	private static async Task<BlameResult> BlameFileAsync(
		HostSession session,
		FilePathRequest request,
		CancellationToken ct) {
		if (WorktreeRelativePath(session, request.Path) is not { } relative) {
			return new BlameResult([], [], [], NotInWorktree(session, request.Path));
		}

		try {
			var blame = await new GitService().BlameFileAsync(session.WorkspaceRoot, relative, ct).ConfigureAwait(false);
			return new BlameResult(
				[.. blame.Commits.Select(c => new BlameCommitWire(
					c.Sha,
					c.Author,
					c.AuthorEmail,
					c.TimeUnix,
					c.Summary,
					c.Uncommitted))],
				blame.LineCommits,
				blame.LineOriginalLines,
				null);
		} catch (GitException ex) {
			return new BlameResult([], [], [], ex.Message);
		}
	}

	private static async Task<CommitHunkResult> CommitHunkAsync(
		HostSession session,
		CommitHunkRequest request,
		CancellationToken ct) {
		if (WorktreeRelativePath(session, request.Path) is not { } relative) {
			return new CommitHunkResult(null, NotInWorktree(session, request.Path));
		}

		if (!GitService.IsCommitSha(request.Sha)) {
			return new CommitHunkResult(null, $"'{request.Sha}' isn't a commit.");
		}

		// Clamping a bad line would answer with the commit's first hunk as though it were this line's change.
		if (request.Line <= 0) {
			return new CommitHunkResult(null, $"Line {request.Line} isn't a line in this file.");
		}

		try {
			var hunk = await new GitService()
				.CommitHunkAsync(session.WorkspaceRoot, request.Sha, relative, request.Line, ct)
				.ConfigureAwait(false);
			return new CommitHunkResult(
				hunk is null ? null : new HunkWire(hunk.Header, hunk.OldStart, hunk.NewStart, hunk.Lines),
				null);
		} catch (GitException ex) {
			return new CommitHunkResult(null, ex.Message);
		}
	}

	private static async Task<HistoryResult> BlameHistoryAsync(
		HostSession session,
		HistoryRequest request,
		CancellationToken ct) {
		if (WorktreeRelativePath(session, request.Path) is not { } relative) {
			return new HistoryResult([], false, NotInWorktree(session, request.Path));
		}

		// A line-scoped walk is anchored at the commit blame attributed the line to, using that commit's line
		// number — the working tree's numbering doesn't address the same line anywhere else in history.
		bool byLine = request.Line > 0;
		if (byLine && !GitService.IsCommitSha(request.Sha)) {
			return new HistoryResult([], false, $"'{request.Sha}' isn't a commit.");
		}

		try {
			var git = new GitService();
			// One over the limit distinguishes "that's all of it" from "there is more", so the web can say which.
			// A file-scoped entry carries no line, so selecting it shows the commit without an area diff — the
			// commit touched the file, not necessarily this line.
			var commits = byLine
				? (await git.LogLinesAsync(session.WorkspaceRoot, request.Sha, relative, request.Line, request.Line, BlameHistoryLimit + 1, ct).ConfigureAwait(false))
					.Select(c => new CommitWire(c.Commit.Sha, c.Commit.Author, c.Commit.TimeUnix, c.Commit.Summary, c.Line))
				: (await git.LogFileAsync(session.WorkspaceRoot, relative, BlameHistoryLimit + 1, ct).ConfigureAwait(false))
					.Select(c => new CommitWire(c.Sha, c.Author, c.TimeUnix, c.Summary, 0));
			var page = commits.ToList();
			return new HistoryResult(
				[.. page.Take(BlameHistoryLimit)],
				page.Count > BlameHistoryLimit,
				null);
		} catch (GitException ex) {
			return new HistoryResult([], false, ex.Message);
		}
	}

	private async Task<CommitRefResult> CommitRefAsync(CommitRefRequest request, CancellationToken ct) {
		if (!GitService.IsCommitSha(request.Sha)
			|| await ResolveOriginRepoAsync(ct).ConfigureAwait(false) is not { } repo) {
			return new CommitRefResult(null, null, null);
		}

		string commitUrl = _pullRequests.CommitUrl(repo, request.Sha);
		try {
			var pullRequest = await _pullRequests.FindForCommitAsync(repo, request.Sha, ct).ConfigureAwait(false);
			return new CommitRefResult(
				commitUrl,
				pullRequest is null ? null : new PullRequestRefWire(pullRequest.Number, pullRequest.Title, pullRequest.Url),
				null);
		} catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) {
			// The commit link needs no credential, so it still stands; only the pull-request lookup failed.
			return new CommitRefResult(commitUrl, null, ex.Message);
		}
	}

	// The web addresses files by absolute path; git wants them relative to the worktree it runs in. A path
	// outside this session's worktree resolves to null rather than reaching git as an unanchored argument.
	private static string? WorktreeRelativePath(HostSession session, string absolutePath) {
		if (string.IsNullOrWhiteSpace(absolutePath)) {
			return null;
		}

		string relative = Path.GetRelativePath(session.WorkspaceRoot, Path.GetFullPath(absolutePath))
			.Replace('\\', '/');
		return relative.Length == 0 || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
			? null
			: relative;
	}

	private static string NotInWorktree(HostSession session, string path) =>
		$"'{path}' isn't inside {session.WorkspaceRoot}.";

	private sealed record FilePathRequest(string Path);

	private sealed record CommitHunkRequest(string Path, string Sha, int Line);

	// Sha + Line address the line inside the commit blame attributed it to; Line 0 asks for the file's history,
	// which needs no anchor.
	private sealed record HistoryRequest(string Path, string Sha, int Line);

	private sealed record CommitRefRequest(string Sha);

	private sealed record BlameCommitWire(
		string Sha,
		string Author,
		string Email,
		long Time,
		string Summary,
		bool Uncommitted);

	private sealed record BlameResult(
		IReadOnlyList<BlameCommitWire> Commits,
		IReadOnlyList<int> LineCommits,
		IReadOnlyList<int> LineOriginals,
		string? Error);

	private sealed record HunkWire(string Header, int OldStart, int NewStart, IReadOnlyList<string> Lines);

	private sealed record CommitHunkResult(HunkWire? Hunk, string? Error);

	private sealed record CommitWire(string Sha, string Author, long Time, string Summary, int Line);

	private sealed record HistoryResult(IReadOnlyList<CommitWire> Commits, bool More, string? Error);

	private sealed record PullRequestRefWire(int Number, string Title, string Url);

	private sealed record CommitRefResult(string? CommitUrl, PullRequestRefWire? PullRequest, string? Error);
}
