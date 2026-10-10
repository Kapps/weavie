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
			"sync", "Syncing the session", WireJson.Default.EmptyPayload, WireJson.Default.SessionSyncResult,
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
			"sessionChanged", "Updating the editor session",
			WireJson.Default.EditorSessionMessage,
			session.View.IsBound,
			(message, _, _) => {
				HandleEditorSessionChanged(session, message.Session, message.Basis);
				return Task.CompletedTask;
			});
		editor.Handle("newScratch", "Creating a scratch file", WireJson.Default.EmptyPayload, (_, _) => {
			session.OpenNewScratch();
			return Task.CompletedTask;
		});
		editor.Handle(
			"saveScratchAs", "Saving a scratch file", WireJson.Default.JsonElement, WireJson.Default.ScratchSaveResult,
			(message, ct) => SaveScratchAsAsync(session, message, ct));
		editor.Handle(
			"saveScratchNamed", "Saving a scratch file", WireJson.Default.JsonElement, WireJson.Default.ScratchSaveResult,
			(message, _) => Task.FromResult(SaveScratchNamed(session, message)));
		editor.Handle("discardScratch", "Discarding a scratch file", WireJson.Default.FilePathMessage, (message, _) => {
			session.Scratch.Delete(message.Path);
			return Task.CompletedTask;
		});

		var review = session.Bus.Feature("review");
		review.Handle("close", "Closing the review", WireJson.Default.EmptyPayload, (_, _) => {
			RunReviewAction(session, () => CloseReview(session));
			return Task.CompletedTask;
		});
		review.Handle("accept", "Accepting changes", WireJson.Default.EmptyPayload, (_, _) => {
			RunReviewAction(session, () => CloseReview(session));
			return Task.CompletedTask;
		});
		review.Handle("revertAll", "Reverting all changes", WireJson.Default.EmptyPayload, (_, _) => {
			UndoTurn(session);
			return Task.CompletedTask;
		});
		review.Handle("revertHunk", "Reverting a change", WireJson.Default.JsonElement, (message, _) => {
			RejectHunk(session, message);
			return Task.CompletedTask;
		});
		review.Handle("keepHunk", "Keeping a change", WireJson.Default.JsonElement, (message, _) => {
			RunReviewAction(session, () => KeepHunk(session, message));
			return Task.CompletedTask;
		});
		review.Handle("unkeepHunk", "Un-keeping a change", WireJson.Default.JsonElement, (message, _) => {
			RunReviewAction(session, () => UnkeepHunk(session, message));
			return Task.CompletedTask;
		});
		review.Handle("revertFile", "Reverting a file", WireJson.Default.JsonElement, (message, _) => {
			RevertFile(session, message);
			return Task.CompletedTask;
		});
		review.Handle("keepFile", "Keeping a file", WireJson.Default.JsonElement, (message, _) => {
			RunReviewAction(session, () => KeepFile(session, message));
			return Task.CompletedTask;
		});
		review.Handle("undo", "Undoing a review action", WireJson.Default.JsonElement, WireJson.Default.ReviewHistoryLocation, (message, _) =>
			Task.FromResult(ReviewUndo(session, message)));
		review.Handle("redo", "Redoing a review action", WireJson.Default.EmptyPayload, WireJson.Default.ReviewHistoryLocation, (_, _) =>
			Task.FromResult(ReviewRedo(session)));
		review.Handle("showFile", "Showing a reviewed file", WireJson.Default.FilePathMessage, (message, _) => {
			PushTurnDiffToWeb(session, message.Path);
			return Task.CompletedTask;
		});
		review.Handle("diffAgainst", "Changing the diff base", WireJson.Default.DiffAgainstMessage, WireJson.Default.ReviewReveal, (message, ct) =>
			DiffAgainstFromWebAsync(session, message.Reference, ct));

		session.Bus.Feature("revise").Handle("start", "Starting a revision", WireJson.Default.ReviseStartMessage, (message, _) => {
			StartRevise(session, message);
			return Task.CompletedTask;
		});

		var files = session.Bus.Feature("files");
		files.Handle(
			"refs", "Listing git refs", WireJson.Default.EmptyPayload, WireJson.Default.DiffRefsResult,
			(_, ct) => ListRefsAsync(session, ct));
		files.HandleOwned(
			"previewGrant", "Opening an HTML preview", WireJson.Default.FilePathRequest, WireJson.Default.PreviewGrant,
			(message, peer, _) => {
				var (grant, baseUrl) = _fileRoutes.GrantPreview(session.Incarnation, peer, message.Path);
				return Task.FromResult(new PreviewGrant(grant, baseUrl));
			});
		files.HandleOwned("previewRelease", "Closing an HTML preview", WireJson.Default.PreviewRelease, (message, peer, _) => {
			_fileRoutes.ReleasePreview(peer, message.Grant);
			return Task.CompletedTask;
		});
		session.Bus.PeerDisconnected += _fileRoutes.ReleasePreviews;
		files.Handle("refreshIndex", "Refreshing the file index", WireJson.Default.EmptyPayload, (_, _) => {
			PushFileIndexToWeb(session, false);
			return Task.CompletedTask;
		});

		session.Bus.Feature("search").Handle(
			"query", "Searching files", WireJson.Default.JsonElement, WireJson.Default.JsonElement,
			(message, ct) => SearchInFilesAsync(session, message, ct));

		var git = session.Bus.Feature("git");
		git.HandleConcurrent(
			"blame", "Loading blame", WireJson.Default.FilePathRequest, WireJson.Default.BlameResult,
			(message, ct) => BlameFileAsync(session, message, ct));
		git.HandleConcurrent(
			"commitHunk", "Loading a commit hunk", WireJson.Default.CommitHunkRequest, WireJson.Default.CommitHunkResult,
			(message, ct) => CommitHunkAsync(session, message, ct));
		git.HandleConcurrent(
			"history", "Loading file history", WireJson.Default.HistoryRequest, WireJson.Default.HistoryResult,
			(message, ct) => BlameHistoryAsync(session, message, ct));
		git.HandleConcurrent(
			"commitRef", "Loading a commit", WireJson.Default.CommitRefRequest, WireJson.Default.CommitRefResult,
			(message, ct) => CommitRefAsync(message, ct));

		var pullRequests = session.Bus.Feature("pullRequests");
		pullRequests.Handle(
			"list", "Listing pull requests", WireJson.Default.PullRequestQuery, WireJson.Default.PullRequestWireArray,
			(message, ct) => ListPullRequestsAsync(message.Query, ct));
		pullRequests.Handle(
			"resolve", "Looking up a pull request", WireJson.Default.PullRequestReference, WireJson.Default.PullRequestWire,
			(message, ct) => GetPullRequestAsync(message, ct));
		pullRequests.Handle(
			"open", "Opening a pull request", WireJson.Default.PullRequestReference, WireJson.Default.CommandWireResult,
			async (message, ct) => CommandWireResult.From(
				await OpenPullRequestAsync(session, message, ct).ConfigureAwait(false)));
		HandlePullRequestComments(session, pullRequests);

		var sources = session.Bus.Feature("sources");
		sources.Handle("open", "Opening a source", WireJson.Default.OpenTargetMessage, (message, _) => {
			OpenTargetForWeb(session, message.Url);
			return Task.CompletedTask;
		});
		sources.Handle(
			"saveToken", "Saving a source token", WireJson.Default.SaveSourceTokenMessage, WireJson.Default.SourceTokenResult,
			(message, ct) => SaveSourceTokenAsync(session, message.SourceId, message.Token, ct));
		sources.Handle("dismissToken", "Dismissing a source token prompt", WireJson.Default.EmptyPayload, (_, _) => {
			DismissSourceTokenPrompt(session);
			return Task.CompletedTask;
		});
		sources.HandleConcurrent(
			"refresh", "Refreshing sources", WireJson.Default.OpenTargetMessage, WireJson.Default.SourceDoc,
			(message, ct) => _sources.FetchAsync(message.Url, ct));
		sources.Handle("saveEdit", "Saving a source edit", WireJson.Default.SourceEditMessage, (message, ct) =>
			SaveSourceEditAsync(session, message.Target, message.OldText, message.NewText, message.EditId, ct));
	}

	private void SyncSession(HostSession session, MessageTarget target) {
		session.Agent.ReplayState(target.Feature("agent"));
		session.ReplayEditor(target.Feature("editor"), line => Log(line));
		session.ReplayWorkspaceFailures(target);
		session.State.Replay(target);
		PushLspConfigToWeb(session, target);
		PostSessionStatus(target, session.Status.Status);
		PushReviewStateToWeb(session, target);
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

	internal sealed record PreviewGrant(string Grant, string Base);

	internal sealed record PreviewRelease(string Grant);

	internal sealed record DiffAgainstMessage(string Reference);

	internal sealed record PullRequestQuery(string Query);

	internal sealed record OpenTargetMessage(string Url);

	internal sealed record SaveSourceTokenMessage(string SourceId, string Token);

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

		public Task<CommandResult> RecreateSessionAsync(string? sessionId, string? agentProviderId, CommandInvocationContext context, CancellationToken ct) =>
			_core.RecreateSessionAsync(_source, sessionId, agentProviderId, context, ct);

		public Task<CommandResult> LoadSessionAsync(string? sessionId, CancellationToken ct) =>
			_core.LoadSessionAsync(sessionId, ct);

		public Task<CommandResult> UnloadSessionAsync(
			string? sessionId,
			CommandInvocationContext context,
			CancellationToken ct) =>
			_core.UnloadSessionAsync(_source, TargetOrSource(sessionId), context, ct);

		public Task<CommandResult> DeleteSessionAsync(
			string? sessionId,
			bool force,
			CommandInvocationContext context,
			CancellationToken ct) =>
			_core.DeleteSessionAsync(_source, TargetOrSource(sessionId), force, context, ct);

		public Task<CommandResult> ClassifyDeleteAsync(string? sessionId, CancellationToken ct) =>
			_core.ClassifyDeleteAsync(TargetOrSource(sessionId), ct);

		private string TargetOrSource(string? sessionId) =>
			string.IsNullOrWhiteSpace(sessionId) ? _source.SlotId : sessionId;
	}
}
