using Weavie.Core.Commands;
using Weavie.Core.Review;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// A session's live PR review comments on the `pullRequests` feature. Core speaks repo-relative paths; the wire
// speaks absolute worktree paths, converted only here.
public sealed partial class HostCore {
	private void HandlePullRequestComments(HostSession session, MessageFeatureChannel pullRequests) {
		var comments = session.PullRequestComments;
		// A thread or review write can change the overview too: a submitted review joins the PR's conversation.
		async Task<CommandWireResult> Write(Task<CommandResult> write, CancellationToken ct) {
			var result = await write.ConfigureAwait(false);
			await session.PullRequestOverview.RefreshAsync(ct).ConfigureAwait(false);
			return CommandWireResult.From(result);
		}

		pullRequests.Handle(
			"comment", "Posting a pull request comment", WireJson.Default.PullRequestCommentRequest, WireJson.Default.CommandWireResult,
			(message, ct) => WorktreeRelativePath(session, message.Path) is { } path
				? Write(comments.CommentAsync(message.Number, message.HeadSha, path, message.Line, message.Body, message.Mode, ct), ct)
				: Task.FromResult(CommandWireResult.From(CommandResult.Failure(NotInWorktree(session, message.Path)))));
		pullRequests.Handle(
			"reply", "Posting a pull request reply", WireJson.Default.PullRequestReplyRequest, WireJson.Default.CommandWireResult,
			(message, ct) => Write(comments.ReplyAsync(message.Number, message.ThreadId, message.Body, message.Mode, ct), ct));
		pullRequests.Handle(
			"editComment", "Editing a pull request comment", WireJson.Default.PullRequestEditRequest, WireJson.Default.CommandWireResult,
			(message, ct) => Write(comments.EditAsync(message.Number, message.Id, message.Body, ct), ct));
		pullRequests.Handle(
			"discardComment", "Discarding a pending comment", WireJson.Default.PullRequestCommentIdRequest, WireJson.Default.CommandWireResult,
			(message, ct) => Write(comments.DiscardCommentAsync(message.Number, message.Id, ct), ct));
		pullRequests.Handle(
			"submitReview", "Submitting a pull request review", WireJson.Default.PullRequestSubmitRequest, WireJson.Default.CommandWireResult,
			(message, ct) => Write(comments.SubmitReviewAsync(message.Number, message.HeadSha, message.Verdict, message.Body, ct), ct));
		pullRequests.Handle(
			"discardReview", "Discarding a pending review", WireJson.Default.PullRequestNumberRequest, WireJson.Default.CommandWireResult,
			(message, ct) => Write(comments.DiscardReviewAsync(message.Number, ct), ct));
		// The web asks while the user can see the PR; its comments and checks aren't tied to the agent's activity.
		pullRequests.Handle("refresh", "Refreshing the pull request", WireJson.Default.EmptyPayload, (_, ct) =>
			Task.WhenAll(comments.RefreshAsync(ct), session.PullRequestOverview.RefreshAsync(ct)));
		pullRequests.Handle(
			"issueComment", "Commenting on the pull request", WireJson.Default.PullRequestIssueCommentRequest, WireJson.Default.CommandWireResult,
			async (message, ct) => CommandWireResult.From(
				await session.PullRequestOverview.CommentAsync(message.Number, message.Body, ct).ConfigureAwait(false)));
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
				set.Review.Viewer,
				set.Review.ViewerIsAuthor,
				set.Review.Pending,
				[.. set.ChangedPaths.Select(path => AbsolutePath(session, path))],
				[.. set.Review.Threads.Select(thread => thread with { Path = AbsolutePath(session, thread.Path) })]),
			snapshot.Error));

	private static void PublishPullRequestOverview(PullRequestOverviewSnapshot snapshot, MessageTarget target) =>
		target.Feature("pullRequests").Publish("overview", WireJson.Default.PullRequestOverviewSnapshot, snapshot);

	private static string AbsolutePath(HostSession session, string relative) =>
		Path.GetFullPath(Path.Combine(session.WorkspaceRoot, relative));

	internal sealed record PullRequestCommentRequest(int Number, string HeadSha, string Path, int Line, string Body, CommentMode Mode);

	internal sealed record PullRequestReplyRequest(int Number, string ThreadId, string Body, CommentMode Mode);

	internal sealed record PullRequestEditRequest(int Number, string Id, string Body);

	internal sealed record PullRequestCommentIdRequest(int Number, string Id);

	internal sealed record PullRequestSubmitRequest(int Number, string HeadSha, ReviewVerdict Verdict, string Body);

	internal sealed record PullRequestNumberRequest(int Number);

	internal sealed record PullRequestIssueCommentRequest(int Number, string Body);

	internal sealed record PullRequestSourcesRequest(string Path, string HeadSha);
}

internal sealed record PullRequestCommentsWire(PullRequestCommentSetWire? Set, string? Error);

internal sealed record PullRequestCommentSetWire(
	int Number,
	string Url,
	string HeadSha,
	ForgeUser Viewer,
	bool ViewerIsAuthor,
	PendingReview? Pending,
	IReadOnlyList<string> ChangedPaths,
	IReadOnlyList<ReviewThread> Threads);
