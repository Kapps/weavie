using Weavie.Core.Commands;
using Weavie.Core.Review;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// A session's live PR review comments on the `pullRequests` feature. Core speaks repo-relative paths; the wire
// speaks absolute worktree paths, converted only here.
public sealed partial class HostCore {
	private void HandlePullRequestComments(HostSession session, MessageFeatureChannel pullRequests) {
		var comments = session.PullRequestComments;
		pullRequests.Handle<PullRequestCommentRequest, CommandWireResult>(
			"comment",
			async (message, ct) => CommandWireResult.From(WorktreeRelativePath(session, message.Path) is { } path
				? await comments.CommentAsync(message.Number, message.HeadSha, path, message.Line, message.Body, ct).ConfigureAwait(false)
				: CommandResult.Failure(NotInWorktree(session, message.Path))));
		pullRequests.Handle<PullRequestReplyRequest, CommandWireResult>(
			"reply",
			async (message, ct) => CommandWireResult.From(
				await comments.ReplyAsync(message.Number, message.InReplyTo, message.Body, ct).ConfigureAwait(false)));
		pullRequests.Handle<PullRequestEditRequest, CommandWireResult>(
			"editComment",
			async (message, ct) => CommandWireResult.From(
				await comments.EditAsync(message.Number, message.Id, message.Body, ct).ConfigureAwait(false)));
		// The web asks while the user can see the comments; a PR's comments aren't tied to the agent's activity.
		pullRequests.Handle<EmptySessionMessage>("refresh", (_, ct) => comments.RefreshAsync(ct));
		pullRequests.HandleConcurrent<PullRequestSourcesRequest, PullRequestSources>(
			"sources",
			(message, ct) => comments.SourcesAsync(
				WorktreeRelativePath(session, message.Path) ?? throw new InvalidOperationException(NotInWorktree(session, message.Path)),
				message.HeadSha,
				ct));
	}

	private static void PublishPullRequestComments(HostSession session, PullRequestCommentsSnapshot snapshot, MessageTarget target) =>
		target.Feature("pullRequests").Publish("comments", new {
			set = snapshot.Set is not { } set ? null : new {
				number = set.Number,
				url = set.Url,
				headSha = set.HeadSha,
				viewer = set.Viewer,
				changedPaths = set.ChangedPaths.Select(path => AbsolutePath(session, path)),
				threads = set.Threads.Select(thread => new {
					rootId = thread.RootId,
					path = AbsolutePath(session, thread.Path),
					line = thread.Line,
					side = thread.Side,
					outdated = thread.Outdated,
					comments = thread.Comments.Select(comment => new {
						id = comment.Id,
						author = comment.Author,
						body = comment.Body,
						createdAt = comment.CreatedAt,
						updatedAt = comment.UpdatedAt,
						mine = set.IsMine(comment),
					}),
				}),
			},
			error = snapshot.Error,
		});

	private static string AbsolutePath(HostSession session, string relative) =>
		Path.GetFullPath(Path.Combine(session.WorkspaceRoot, relative));

	private sealed record PullRequestCommentRequest(int Number, string HeadSha, string Path, int Line, string Body);

	private sealed record PullRequestReplyRequest(int Number, long InReplyTo, string Body);

	private sealed record PullRequestEditRequest(int Number, long Id, string Body);

	private sealed record PullRequestSourcesRequest(string Path, string HeadSha);
}
