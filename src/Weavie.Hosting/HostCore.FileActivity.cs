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
				if (fact is InventoryChanged changed) {
					PushFileIndexChangeToWeb(session, changed.Change);
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
				BufferSaved saved => JoinAndRefreshReviewAsync(session, saved.Path, CancellationToken.None),
				FileChanged changed => JoinAndRefreshReviewAsync(session, changed.Path, CancellationToken.None),
				FileDeleted deleted => JoinAndRefreshReviewAsync(session, deleted.Path, CancellationToken.None),
				FilesInvalidated invalidated => InvalidateReview(session, invalidated),
				_ => Task.CompletedTask,
			},
			OnFailure);

		session.FileActivity.Subscribe(
			"git status projection",
			fact => fact is InventoryChanged ? Task.CompletedTask : InvokeForSessionAsync(() => PushGitStatus(session)),
			OnFailure);
	}

	private async Task JoinAndRefreshReviewAsync(HostSession session, string path, CancellationToken ct) {
		await JoinReviewAsync(session, path, ct).ConfigureAwait(false);
		await RefreshReviewAsync(session, path).ConfigureAwait(false);
	}

	// The watcher echoes every save and agent edit the review already shows; anything else re-lists it from git.
	private static Task InvalidateReview(HostSession session, FilesInvalidated invalidated) {
		if (session.Changes.Review is not null && !session.Changes.AccountsFor(invalidated.Changes.Select(change => change.Path))) {
			session.ReviewListing.Invalidate();
			session.ReviewHead.RequestRefresh();
		}
		return Task.CompletedTask;
	}

	// Built as it is published, on the dispatcher, so it can never land after a newer push a review action made there
	// — the page fetches baselines by the revisions in the last diff it saw. History before diff/changes: see
	// HostCore.WebBridge.ApplyHistoryResult's doc comment on why.
	private Task RefreshReviewAsync(HostSession session, string path) =>
		InvokeForSessionAsync(() => {
			PushReviewHistoryToWeb(session);
			PushTurnDiffToWeb(session, path);
			PushTurnChangesToWeb(session);
		});

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
