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
