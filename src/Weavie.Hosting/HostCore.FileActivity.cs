using Weavie.Core.FileActivity;
using Weavie.Core.Lsp;

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
				BufferSaved saved => RefreshReviewAsync(session, saved.Path, deleted: false),
				FileChanged changed => RefreshReviewAsync(session, changed.Path, deleted: false),
				FileDeleted deleted => RefreshReviewAsync(session, deleted.Path, deleted: true),
				_ => Task.CompletedTask,
			},
			OnFailure);
	}

	private Task RefreshReviewAsync(HostSession session, string path, bool deleted) =>
		InvokeForSessionAsync(() => {
			if (!deleted) {
				PushTurnDiffToWeb(session, path);
			}
			PushTurnChangesToWeb(session);
			PushReviewHistoryToWeb(session);
		});

	private Task FileActivityFailedAsync(HostSession session, FileActivityFailure failure) =>
		InvokeForSessionAsync(
			() => Notify(
				session,
				"warn",
				$"{failure.Consumer} failed while updating file activity: {failure.Error.Message}"));

	private Task InvokeForSessionAsync(Action action) =>
		_ui.InvokeAsync(() => {
			action();
			return Task.CompletedTask;
		}, CancellationToken.None);

}
