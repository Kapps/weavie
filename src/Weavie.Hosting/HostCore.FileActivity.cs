using Weavie.Core.Changes;
using Weavie.Core.FileActivity;
using Weavie.Core.Lsp;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private void WireFileActivity(HostSession session) {
		Task OnFailure(FileActivityFailure failure) => FileActivityFailedAsync(session, failure);

		session.FileActivity.Subscribe(
			"editor file projection",
			fact => fact switch {
				FileChanged changed => InvokeForSessionAsync(() => PushRefreshToWeb(session, changed.Path)),
				FileDeleted deleted => InvokeForSessionAsync(() => PushDeletionToWeb(session, deleted.Path)),
				FilesInvalidated invalidated => InvokeForSessionAsync(
					() => PushWatcherChangesToWeb(session, invalidated.Changes)),
				_ => Task.CompletedTask,
			},
			OnFailure);

		session.FileActivity.Subscribe(
			"file index projection",
			fact => {
				if (fact is FilesInvalidated invalidated && invalidated.Changes.Any(
					change => change.Kind != FileInvalidationKind.Changed)) {
					PushCachedFileIndexToWeb(session);
				}
				return Task.CompletedTask;
			},
			OnFailure);

		session.FileActivity.Subscribe(
			"language server invalidation",
			fact => {
				if (fact is FilesInvalidated invalidated) {
					session.Lsp.NotifyWatchedFileChanges(LspFileChanges.FromInvalidations(invalidated.Changes));
				}
				return Task.CompletedTask;
			},
			OnFailure);

		session.FileActivity.Subscribe(
			"review presentation",
			fact => fact switch {
				BufferSaved saved => RefreshReviewAsync(session, saved.Path),
				FileChanged changed => RefreshReviewAsync(session, changed.Path),
				FileDeleted deleted => RefreshReviewAsync(session, deleted.Path),
				_ => Task.CompletedTask,
			},
			OnFailure);

		session.FileActivity.Subscribe(
			"git status projection",
			_ => InvokeForSessionAsync(() => PushGitStatus(session)),
			OnFailure);
	}

	private Task RefreshReviewAsync(HostSession session, string path) =>
		session.ReviewPublication.RunAsync(() => {
			var payloads = ReviewPayloads.Build(session, path, ActiveReview(session)?.Label ?? string.Empty);
			return payloads.PublishToAsync(session.Bus.BroadcastTarget);
		}, CancellationToken.None);

	// One save's review projection: the undo/redo state, the saved file's diff (absent when it was deleted or
	// isn't in the turn), and the changed-file list. History before diff/changes — see
	// HostCore.WebBridge.ApplyHistoryResult's doc comment on why.
	private readonly record struct ReviewPayloads(string History, string? Diff, string Changes) {
		public static ReviewPayloads Build(HostSession session, string path, string label) => new(
			ChangeMessages.ReviewHistory(session.Changes),
			session.Changes.GetTurn(path) is not { } turn ? null : ChangeMessages.TurnDiff(turn),
			ChangeMessages.TurnChanges(session.Changes, label));

		public async Task PublishToAsync(MessageTarget target) {
			var review = target.Feature("review");
			await review.PublishJsonAsync("history", History, CancellationToken.None).ConfigureAwait(false);
			if (Diff is not null) {
				await review.PublishJsonAsync("diff", Diff, CancellationToken.None).ConfigureAwait(false);
			}

			await review.PublishJsonAsync("changes", Changes, CancellationToken.None).ConfigureAwait(false);
		}
	}

	private Task FileActivityFailedAsync(HostSession session, FileActivityFailure failure) =>
		InvokeForSessionAsync(() => {
			string message = $"{failure.Consumer} failed while updating file activity: {failure.Error.Message}";
			if (!session.EndIfWorkspaceRootIsGone(message)) {
				Notify(session, "warn", message);
			}
		});

	private Task InvokeForSessionAsync(Action action) =>
		_ui.InvokeAsync(() => {
			action();
			return Task.CompletedTask;
		}, CancellationToken.None);

}
