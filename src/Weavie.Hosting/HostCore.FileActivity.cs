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

		session.FileActivity.Subscribe(
			"git status projection",
			_ => InvokeForSessionAsync(() => PushGitStatus(session)),
			OnFailure);
	}

	private Task RefreshReviewAsync(HostSession session, string path, bool deleted) {
		// Built on this consumer's own thread, never the dispatcher's: these payloads carry whole-file diffs, and
		// the dispatcher is the thread the desktop hosts deliver the user's keystrokes on.
		var payloads = ReviewPayloads.Build(session, path, deleted, ActiveReview(session)?.Label ?? string.Empty);
		return InvokeForSessionAsync(() => payloads.PublishTo(session.Bus.BroadcastTarget));
	}

	// One save's review projection: the undo/redo state, the saved file's diff (absent when it was deleted or
	// isn't in the turn), and the changed-file list. History before diff/changes — see
	// HostCore.WebBridge.ApplyHistoryResult's doc comment on why.
	private readonly record struct ReviewPayloads(string History, string? Diff, string Changes) {
		public static ReviewPayloads Build(HostSession session, string path, bool deleted, string label) => new(
			ChangeMessages.ReviewHistory(session.Changes),
			deleted || session.Changes.GetTurn(path) is not { } turn ? null : ChangeMessages.TurnDiff(turn),
			ChangeMessages.TurnChanges(session.Changes, label));

		public void PublishTo(MessageTarget target) {
			var review = target.Feature("review");
			review.PublishJson("history", History);
			if (Diff is not null) {
				review.PublishJson("diff", Diff);
			}

			review.PublishJson("changes", Changes);
		}
	}

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
