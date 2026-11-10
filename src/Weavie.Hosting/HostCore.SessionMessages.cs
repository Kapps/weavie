using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Editor;
using Weavie.Core.Sessions;
using Weavie.Core.Sources;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private void WireCoreSessionMessages(HostSession session) {
		var lifecycle = session.Bus.Feature("lifecycle");
		lifecycle.HandleOwned(
			"sync", MessageActivity.SessionCritical("Syncing the session"), WireJson.Default.EmptyPayload, WireJson.Default.SessionSyncResult,
			(_, peer, _) => {
				SyncSession(session, peer.Target);
				return Task.FromResult(new SessionSyncResult(true));
			});

		session.Bus.Feature("commands").HandleKeyedAfterResponse(
			"invoke", CommandActivity, WireJson.Default.CommandRequest, WireJson.Default.CommandWireResult,
			CommandExecutionLane,
			async (message, ct) => {
				var execution = await PrepareCommandAsync(
					session,
					message.Id,
					RawJson(message.Args),
					ct).ConfigureAwait(false);
				return new ResponseWithCompletion<CommandWireResult>(
					CommandWireResult.From(execution.Result),
					execution.CompleteAsync);
			});

		var editor = session.Bus.Feature("editor");
		editor.HandleOwned(
			"sessionChanged", MessageActivity.SessionCritical("Updating the editor session"),
			WireJson.Default.EditorSessionMessage,
			session.View.IsBound,
			(message, _, _) => {
				HandleEditorSessionChanged(session, message.Session, message.Basis);
				return Task.CompletedTask;
			});
		editor.Handle("newScratch", MessageActivity.ActionFailed("Creating a scratch file"), WireJson.Default.EmptyPayload, (_, _) => {
			session.OpenNewScratch();
			return Task.CompletedTask;
		});
		editor.HandleConcurrent(
			"saveScratchAs", MessageActivity.NoDeadline("Saving a scratch file"), WireJson.Default.JsonElement, WireJson.Default.ScratchSaveResult,
			(message, ct) => SaveScratchAsAsync(session, message, ct));
		editor.Handle(
			"saveScratchNamed", MessageActivity.ActionFailed("Saving a scratch file"), WireJson.Default.JsonElement, WireJson.Default.ScratchSaveResult,
			(message, _) => Task.FromResult(SaveScratchNamed(session, message)));
		editor.Handle("discardScratch", MessageActivity.ActionFailed("Discarding a scratch file"), WireJson.Default.FilePathMessage, (message, _) => {
			session.Scratch.Delete(message.Path);
			return Task.CompletedTask;
		});

		var review = session.Bus.Feature("review");
		review.Handle("close", MessageActivity.ActionFailed("Closing the review"), WireJson.Default.EmptyPayload, (_, _) => {
			RunReviewAction(session, () => CloseReview(session));
			return Task.CompletedTask;
		});
		review.Handle("accept", MessageActivity.ActionFailed("Accepting changes"), WireJson.Default.EmptyPayload, (_, _) => {
			RunReviewAction(session, () => CloseReview(session));
			return Task.CompletedTask;
		});
		review.Handle("revertAll", MessageActivity.SessionCritical("Reverting all changes"), WireJson.Default.EmptyPayload, (_, _) => {
			UndoTurn(session);
			return Task.CompletedTask;
		});
		review.Handle("revertHunk", MessageActivity.SessionCritical("Reverting a change"), WireJson.Default.JsonElement, (message, _) => {
			RejectHunk(session, message);
			return Task.CompletedTask;
		});
		review.Handle("keepHunk", MessageActivity.ActionFailed("Keeping a change"), WireJson.Default.JsonElement, (message, _) => {
			RunReviewAction(session, () => KeepHunk(session, message));
			return Task.CompletedTask;
		});
		review.Handle("unkeepHunk", MessageActivity.ActionFailed("Un-keeping a change"), WireJson.Default.JsonElement, (message, _) => {
			RunReviewAction(session, () => UnkeepHunk(session, message));
			return Task.CompletedTask;
		});
		review.Handle("revertFile", MessageActivity.SessionCritical("Reverting a file"), WireJson.Default.JsonElement, (message, _) => {
			RevertFile(session, message);
			return Task.CompletedTask;
		});
		review.Handle("keepFile", MessageActivity.ActionFailed("Keeping a file"), WireJson.Default.JsonElement, (message, _) => {
			RunReviewAction(session, () => KeepFile(session, message));
			return Task.CompletedTask;
		});
		review.Handle("undo", MessageActivity.SessionCritical("Undoing a review action"), WireJson.Default.JsonElement, WireJson.Default.ReviewHistoryLocation, (message, _) =>
			Task.FromResult(RunReviewAction(session, () => ReviewUndo(session, message))));
		review.Handle("redo", MessageActivity.SessionCritical("Redoing a review action"), WireJson.Default.EmptyPayload, WireJson.Default.ReviewHistoryLocation, (_, _) =>
			Task.FromResult(RunReviewAction(session, () => ReviewRedo(session))));
		review.Handle("showFile", MessageActivity.ActionFailed("Showing a reviewed file"), WireJson.Default.FilePathMessage, (message, _) => {
			PushTurnDiffToWeb(session, message.Path);
			return Task.CompletedTask;
		});
		review.HandleConcurrent(
			"text", MessageActivity.ActionFailed("Loading a review baseline"), WireJson.Default.ReviewTextRequest, WireJson.Default.ReviewTextResult,
			(message, _) => Task.FromResult(new ReviewTextResult(session.Changes.ReviewText(message.Path, message.Revision))));
		review.Handle("diffAgainst", MessageActivity.ActionFailed("Changing the diff base"), WireJson.Default.DiffAgainstMessage, WireJson.Default.ReviewOpened, (message, ct) =>
			ChooseDiffAgainstFromWebAsync(session, message.Reference, message.Replaces, ct));
		review.Handle("diffAgainstBase", MessageActivity.ActionFailed("Reviewing the branch"), WireJson.Default.DiffAgainstBaseMessage, WireJson.Default.ReviewOpened, (message, ct) =>
			DiffAgainstBaseFromWebAsync(session, message.Replaces, ct));

		session.Bus.Feature("revise").Handle("start", MessageActivity.ActionFailed("Starting a revision"), WireJson.Default.ReviseStartMessage, (message, _) => {
			StartRevise(session, message);
			return Task.CompletedTask;
		});

		var files = session.Bus.Feature("files");
		files.Handle(
			"refs", MessageActivity.ActionFailed("Listing git refs"), WireJson.Default.EmptyPayload, WireJson.Default.DiffRefsResult,
			(_, ct) => ListRefsAsync(session, ct));
		files.HandleOwned(
			"previewGrant", MessageActivity.ActionFailed("Opening an HTML preview"), WireJson.Default.FilePathRequest, WireJson.Default.PreviewGrant,
			(message, peer, _) => {
				var (grant, baseUrl) = _fileRoutes.GrantPreview(session.Incarnation, peer, message.Path);
				return Task.FromResult(new PreviewGrant(grant, baseUrl));
			});
		files.HandleOwned("previewRelease", MessageActivity.ActionFailed("Closing an HTML preview"), WireJson.Default.PreviewRelease, (message, peer, _) => {
			_fileRoutes.ReleasePreview(peer, message.Grant);
			return Task.CompletedTask;
		});
		session.Bus.PeerDisconnected += _fileRoutes.ReleasePreviews;
		files.Handle("refreshIndex", MessageActivity.Droppable("Refreshing the file index"), WireJson.Default.EmptyPayload, (_, _) => {
			PushFileIndexToWeb(session, false);
			return Task.CompletedTask;
		});

		session.Bus.Feature("search").Handle(
			"query", MessageActivity.ActionFailed("Searching files"), WireJson.Default.JsonElement, WireJson.Default.JsonElement,
			(message, ct) => SearchInFilesAsync(session, message, ct));

		var git = session.Bus.Feature("git");
		git.HandleConcurrent(
			"blame", MessageActivity.Droppable("Loading blame"), WireJson.Default.FilePathRequest, WireJson.Default.BlameResult,
			(message, ct) => BlameFileAsync(session, message, ct));
		git.HandleConcurrent(
			"commitHunk", MessageActivity.ActionFailed("Loading a commit hunk"), WireJson.Default.CommitHunkRequest, WireJson.Default.CommitHunkResult,
			(message, ct) => CommitHunkAsync(session, message, ct));
		git.HandleConcurrent(
			"history", MessageActivity.ActionFailed("Loading file history"), WireJson.Default.HistoryRequest, WireJson.Default.HistoryResult,
			(message, ct) => BlameHistoryAsync(session, message, ct));
		git.HandleConcurrent(
			"commitRef", MessageActivity.ActionFailed("Loading a commit"), WireJson.Default.CommitRefRequest, WireJson.Default.CommitRefResult,
			(message, ct) => CommitRefAsync(message, ct));

		var pullRequests = session.Bus.Feature("pullRequests");
		// Lookups are read-only and the dialog keeps only its latest answer, so they never queue behind an open.
		pullRequests.HandleConcurrent(
			"list", MessageActivity.ActionFailed("Listing pull requests"), WireJson.Default.PullRequestQuery, WireJson.Default.PullRequestWireArray,
			(message, ct) => ListPullRequestsAsync(message.Query, ct));
		pullRequests.HandleConcurrent(
			"resolve", MessageActivity.ActionFailed("Looking up a pull request"), WireJson.Default.PullRequestReference, WireJson.Default.PullRequestWire,
			(message, ct) => GetPullRequestAsync(message, ct));
		pullRequests.HandleKeyed(
			"open", _ => MessageActivity.NoDeadline("Opening a pull request"), WireJson.Default.PullRequestReference, WireJson.Default.CommandWireResult,
			message => $"open:{message.Owner}/{message.Repo}#{message.Number}",
			async (message, ct) => CommandWireResult.From(
				await OpenPullRequestAsync(session, message, ct).ConfigureAwait(false)));
		HandlePullRequestComments(session, pullRequests);

		var sources = session.Bus.Feature("sources");
		sources.Handle("open", MessageActivity.ActionFailed("Opening a source"), WireJson.Default.OpenTargetMessage, (message, _) => {
			OpenTargetForWeb(session, message.Url);
			return Task.CompletedTask;
		});
		sources.HandleConcurrent(
			"refresh", MessageActivity.Droppable("Refreshing sources"), WireJson.Default.OpenTargetMessage, WireJson.Default.SourceDoc,
			(message, ct) => _sources.FetchAsync(message.Url, ct));
		sources.Handle("saveEdit", MessageActivity.ActionFailed("Saving a source edit"), WireJson.Default.SourceEditMessage, (message, ct) =>
			SaveSourceEditAsync(session, message.Target, message.OldText, message.NewText, message.EditId, ct));
	}

	private void SyncSession(HostSession session, MessageTarget target) {
		session.Agent.ReplayState(target.Feature("agent"));
		session.ReplayEditor(target.Feature("editor"), line => Log(line));
		session.ReplayWorkspaceFailures(target);
		session.ReplayUnsyncedReviewHintErrors(target);
		session.State.Replay(target);
		PushLspConfigToWeb(session, target);
		PostSessionStatus(target, session.Status.Status);
		PostSessionActivity(target, session.Activity.Line);
		PostSessionRun(target, SessionRunLine(session));
		PushReviewStateToWeb(session, target);
		PushReviewCommits(session, target);
		session.DiffPresenter.Replay(target.Feature("editor"));
		PushFileIndexToWeb(session, true, target);
		PushGitStatus(session, target);
		PushPullRequestStatus(session, target);
		PushRefLinkBase(session, target);
		session.Claude?.ResyncPane(target.Feature("terminal.agent"));
		session.Agent.AuthenticationTerminal?.Controller.ResyncPane(target.Feature("terminal.agent"));
		session.Shells.Resync(target);
	}

	private static CommandResult FromWireResult(CommandWireResult result) =>
		new(result.Ok, result.Message, result.Error) {
			DataJson = RawJson(result.Data),
		};

	private static string? RawJson(JsonElement? value) =>
		value is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } element
			? element.GetRawText()
			: null;

	internal sealed record SessionSyncResult(bool Ok);

	internal sealed record CommandRequest(string Id, JsonElement? Args);

	internal sealed record HostBranchPreviewRequest(
		string? SourceId,
		string? Prompt,
		IReadOnlyList<NewSessionAttachment> Attachments,
		bool UserInitiated);

	internal sealed record BranchPreviewResult(string Branch, string? Error, bool NeedsMoreDetail) {
		public static BranchPreviewResult MoreDetail { get; } = new(string.Empty, null, true);

		public static BranchPreviewResult Named(string branch) => new(branch, null, false);

		public static BranchPreviewResult Failed(string error) => new(string.Empty, error, false);
	}

	internal sealed record EditorSessionMessage(JsonElement Session, long Basis);

	internal sealed record EditorFlushResult(JsonElement Session, long Basis);

	internal sealed record FilePathMessage(string Path);

	internal sealed record ReviewTextRequest(string Path, string Revision);

	/// <summary>A review baseline's text, or null once its revision is superseded by a newer turn diff.</summary>
	internal sealed record ReviewTextResult(string? Text);

	internal sealed record PreviewGrant(string Grant, string Base);

	internal sealed record PreviewRelease(string Grant);

	/// <summary>Diff against <c>Reference</c>; <c>Replaces</c> names the open review the user agreed to close, if any.</summary>
	internal sealed record DiffAgainstMessage(string Reference, string? Replaces);

	/// <summary>Review the whole branch; <c>Replaces</c> as in <see cref="DiffAgainstMessage"/>.</summary>
	internal sealed record DiffAgainstBaseMessage(string? Replaces);

	internal sealed record PullRequestQuery(string Query);

	internal sealed record OpenTargetMessage(string Url);


	internal sealed record SourceEditMessage(string Target, string OldText, string NewText, string EditId);

	private sealed class BoundSessionHost : ISessionHost {
		private readonly HostCore _core;
		private readonly HostSession _source;

		public BoundSessionHost(HostCore core, HostSession source) {
			_core = core;
			_source = source;
		}

		public Task<CommandResult> NewSessionAsync(NewSessionRequest request, CancellationToken ct) =>
			_core.NewSessionAsync(_source.Address, request, ct);

		public Task<CommandResult> ForkSessionAsync(ForkSessionRequest request, CancellationToken ct) =>
			_core.ForkSessionAsync(_source, request, ct);

		public Task<CommandResult> RecreateSessionAsync(
			string? sessionId, string? agentProviderId, bool stopBackgroundWork, CommandInvocationContext context, CancellationToken ct) =>
			_core.RecreateSessionAsync(_source, sessionId, agentProviderId, stopBackgroundWork, context, ct);

		public Task<CommandResult> ReloadSessionAsync(
			string? sessionId, bool stopBackgroundWork, CommandInvocationContext context, CancellationToken ct) =>
			_core.ReloadSessionAsync(_source, sessionId, stopBackgroundWork, context, ct);

		public Task<CommandResult> CancelOperationAsync(string? operationId) => _core.CancelOperationAsync(operationId);

		public Task<CommandResult> LoadSessionAsync(string? sessionId, CancellationToken ct) =>
			_core.LoadSessionAsync(sessionId, ct);

		public Task<CommandResult> UnloadSessionAsync(
			string? sessionId,
			bool stopBackgroundWork,
			CommandInvocationContext context,
			CancellationToken ct) =>
			_core.UnloadSessionAsync(_source, TargetOrSource(sessionId), stopBackgroundWork, context, ct);

		public Task<CommandResult> DeleteSessionAsync(
			string? sessionId,
			bool force,
			bool stopBackgroundWork,
			CommandInvocationContext context,
			CancellationToken ct) =>
			_core.DeleteSessionAsync(_source, TargetOrSource(sessionId), force, stopBackgroundWork, context, ct);

		public Task<CommandResult> ClassifyDeleteAsync(string? sessionId, CancellationToken ct) =>
			_core.ClassifyDeleteAsync(TargetOrSource(sessionId), ct);

		private string TargetOrSource(string? sessionId) =>
			string.IsNullOrWhiteSpace(sessionId) ? _source.SlotId : sessionId;
	}
}
