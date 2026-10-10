using Weavie.Core.Commands;
using Weavie.Core.Review;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// A session's live PR review comments on the `pullRequests` feature. Core speaks repo-relative paths; the wire
// speaks absolute worktree paths, converted only here.
public sealed partial class HostCore {
	private void HandlePullRequestComments(HostSession session, MessageFeatureChannel pullRequests) {
		var comments = session.PullRequestComments;
		pullRequests.Handle(
			"comment", "Posting a pull request comment", WireJson.Default.PullRequestCommentRequest, WireJson.Default.CommandWireResult,
			async (message, ct) => CommandWireResult.From(WorktreeRelativePath(session, message.Path) is { } path
				? await comments.CommentAsync(message.Number, message.HeadSha, path, message.Line, message.Body, ct).ConfigureAwait(false)
				: CommandResult.Failure(NotInWorktree(session, message.Path))));
		pullRequests.Handle(
			"reply", "Posting a pull request reply", WireJson.Default.PullRequestReplyRequest, WireJson.Default.CommandWireResult,
			async (message, ct) => CommandWireResult.From(
				await comments.ReplyAsync(message.Number, message.InReplyTo, message.Body, ct).ConfigureAwait(false)));
		pullRequests.Handle(
			"editComment", "Editing a pull request comment", WireJson.Default.PullRequestEditRequest, WireJson.Default.CommandWireResult,
			async (message, ct) => CommandWireResult.From(
				await comments.EditAsync(message.Number, message.Id, message.Body, ct).ConfigureAwait(false)));
		// The web asks while the user can see the comments; a PR's comments aren't tied to the agent's activity.
		pullRequests.Handle("refresh", "Refreshing pull request comments", WireJson.Default.EmptyPayload, (_, ct) => comments.RefreshAsync(ct));
		pullRequests.HandleConcurrent(
			"sources", "Loading pull request sources", WireJson.Default.PullRequestSourcesRequest, WireJson.Default.PullRequestSources,
			(message, ct) => comments.SourcesAsync(
				WorktreeRelativePath(session, message.Path) ?? throw new InvalidOperationException(NotInWorktree(session, message.Path)),
				message.HeadSha,
				ct));
	}

	private static void PublishPullRequestComments(HostSession session, PullRequestCommentsSnapshot snapshot, MessageTarget target) =>
		target.Feature("pullRequests").Publish("comments", WireJson.Default.PullRequestCommentsWire, new(
			snapshot.Set is not { } set ? null : new PullRequestCommentSetWire(
				set.Number,
				set.Url,
				set.HeadSha,
				set.Viewer,
				[.. set.ChangedPaths.Select(path => AbsolutePath(session, path))],
				[.. set.Threads.Select(thread => new PullRequestThreadWire(
					thread.RootId,
					AbsolutePath(session, thread.Path),
					thread.Line,
					thread.Side,
					thread.Outdated,
					[.. thread.Comments.Select(comment => new PullRequestCommentWire(
						comment.Id,
						comment.Author,
						comment.AuthorAvatarUrl,
						comment.Url,
						comment.Body,
						comment.CreatedAt,
						comment.UpdatedAt,
						set.IsMine(comment)))]))]),
			snapshot.Error));

	private static string AbsolutePath(HostSession session, string relative) =>
		Path.GetFullPath(Path.Combine(session.WorkspaceRoot, relative));

	internal sealed record PullRequestCommentRequest(int Number, string HeadSha, string Path, int Line, string Body);

	internal sealed record PullRequestReplyRequest(int Number, long InReplyTo, string Body);

	internal sealed record PullRequestEditRequest(int Number, long Id, string Body);

	internal sealed record PullRequestSourcesRequest(string Path, string HeadSha);
}

internal sealed record PullRequestCommentsWire(PullRequestCommentSetWire? Set, string? Error);

internal sealed record PullRequestCommentSetWire(
	int Number,
	string Url,
	string HeadSha,
	ForgeUser Viewer,
	IReadOnlyList<string> ChangedPaths,
	IReadOnlyList<PullRequestThreadWire> Threads);

internal sealed record PullRequestThreadWire(
	long RootId,
	string Path,
	int Line,
	string Side,
	bool Outdated,
	IReadOnlyList<PullRequestCommentWire> Comments);

internal sealed record PullRequestCommentWire(
	long Id,
	string Author,
	string AvatarUrl,
	string Url,
	string Body,
	string CreatedAt,
	string UpdatedAt,
	bool Mine);
