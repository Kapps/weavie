using System.Text.Json;
using Weavie.Core.Commands;
using Weavie.Core.Editor;
using Weavie.Core.Sessions;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private void WireCoreSessionMessages(HostSession session) {
		var lifecycle = session.Bus.Feature("lifecycle");
		lifecycle.HandleOwned<SessionSyncRequest, SessionSyncResult>(
			"sync",
			(_, peer, _) => {
				SyncSession(session, peer.Target);
				return Task.FromResult(new SessionSyncResult(true));
			});

		session.Bus.Feature("commands").HandleAfterResponse<CommandRequest, CommandWireResult>(
			"invoke",
			async (message, ct) => {
				var execution = await PrepareCommandAsync(
					session,
					message.Id,
					RawJson(message.Args),
					ct).ConfigureAwait(false);
				return new ResponseWithCompletion<CommandWireResult>(
					ToWireResult(execution.Result),
					execution.CompleteAsync);
			});

		var editor = session.Bus.Feature("editor");
		editor.HandleOwned<EditorSessionMessage>(
			"sessionChanged",
			session.View.IsBound,
			(message, _, _) => {
				HandleEditorSessionChanged(session, message.Session);
				return Task.CompletedTask;
			});
		editor.Handle<EmptySessionMessage>("newScratch", (_, _) => {
			session.OpenNewScratch();
			return Task.CompletedTask;
		});
		editor.Handle<JsonElement, ScratchSaveResult>(
			"saveScratchAs",
			(message, ct) => SaveScratchAsAsync(session, message, ct));
		editor.Handle<JsonElement, ScratchSaveResult>(
			"saveScratchNamed",
			(message, _) => Task.FromResult(SaveScratchNamed(session, message)));
		editor.Handle<FilePathMessage>("discardScratch", (message, _) => {
			session.Scratch.Delete(message.Path);
			return Task.CompletedTask;
		});

		var review = session.Bus.Feature("review");
		review.Handle<EmptySessionMessage>("accept", (_, _) => {
			AcceptTurn(session);
			return Task.CompletedTask;
		});
		review.Handle<EmptySessionMessage>("revertAll", (_, _) => {
			UndoTurn(session);
			return Task.CompletedTask;
		});
		review.Handle<JsonElement>("revertHunk", (message, _) => {
			RejectHunk(session, message);
			return Task.CompletedTask;
		});
		review.Handle<JsonElement>("keepHunk", (message, _) => {
			KeepHunk(session, message);
			return Task.CompletedTask;
		});
		review.Handle<JsonElement>("unkeepHunk", (message, _) => {
			UnkeepHunk(session, message);
			return Task.CompletedTask;
		});
		review.Handle<JsonElement>("revertFile", (message, _) => {
			RevertFile(session, message);
			return Task.CompletedTask;
		});
		review.Handle<JsonElement>("keepFile", (message, _) => {
			KeepFile(session, message);
			return Task.CompletedTask;
		});
		review.Handle<JsonElement>("undo", (message, _) => {
			ReviewUndo(session, message);
			return Task.CompletedTask;
		});
		review.Handle<EmptySessionMessage>("redo", (_, _) => {
			ReviewRedo(session);
			return Task.CompletedTask;
		});
		review.Handle<FilePathMessage>("showFile", (message, _) => {
			PushReviewFileToWeb(session, message.Path);
			return Task.CompletedTask;
		});
		review.Handle<DiffAgainstMessage>("diffAgainst", (message, ct) =>
			DiffAgainstFromWebAsync(session, message.Reference, ct));
		review.Handle<ReviewCommentRequest, CommandWireResult>(
			"addComment",
			async (message, ct) => ToWireResult(
				await AddPrCommentAsync(session, message, ct).ConfigureAwait(false)));

		var files = session.Bus.Feature("files");
		files.Handle<EmptySessionMessage, string[]>(
			"refs",
			(_, ct) => ListRefsAsync(session, ct));
		files.Handle<EmptySessionMessage>("refreshIndex", (_, _) => {
			PushFileIndexToWeb(session, false);
			return Task.CompletedTask;
		});

		session.Bus.Feature("search").Handle<JsonElement, JsonElement>(
			"query",
			(message, ct) => SearchInFilesAsync(session, message, ct));

		var pullRequests = session.Bus.Feature("pullRequests");
		pullRequests.Handle<PullRequestQuery, PullRequestWire[]>(
			"list",
			(message, ct) => ListPullRequestsAsync(message.Query, ct));
		pullRequests.Handle<PullRequestReference, PullRequestWire?>(
			"resolve",
			(message, ct) => GetPullRequestAsync(message, ct));
		pullRequests.Handle<PullRequestReference, CommandWireResult>(
			"open",
			async (message, ct) => ToWireResult(
				await OpenPullRequestAsync(session, message, ct).ConfigureAwait(false)));

		var sources = session.Bus.Feature("sources");
		sources.Handle<OpenTargetMessage>("open", (message, _) => {
			OpenTargetForWeb(session, message.Url);
			return Task.CompletedTask;
		});
		sources.Handle<SaveSourceTokenMessage, SourceTokenResult>(
			"saveToken",
			(message, ct) => SaveSourceTokenAsync(session, message.SourceId, message.Token, ct));
		sources.Handle<EmptySessionMessage>("dismissToken", (_, _) => {
			DismissSourceTokenPrompt(session);
			return Task.CompletedTask;
		});
		sources.Handle<SourceEditMessage>("saveEdit", (message, ct) =>
			SaveSourceEditAsync(session, message.Target, message.OldText, message.NewText, ct));
	}

	private void SyncSession(HostSession session, MessageTarget target) {
		session.ReplayEditor(target.Feature("editor"), line => Log(line));
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
		session.Agent.ReplayState(target.Feature("agent"));
		session.Shell.ResyncPane(target.Feature("terminal.shell"));
	}

	private static CommandWireResult ToWireResult(CommandResult result) {
		JsonElement? data = null;
		if (!string.IsNullOrWhiteSpace(result.DataJson)) {
			using var document = JsonDocument.Parse(result.DataJson);
			data = document.RootElement.Clone();
		}

		return new CommandWireResult(result.Ok, result.Message, result.Error, data);
	}

	private static CommandResult FromWireResult(CommandWireResult result) =>
		new(result.Ok, result.Message, result.Error) {
			DataJson = RawJson(result.Data),
		};

	private static string? RawJson(JsonElement? value) =>
		value is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } element
			? element.GetRawText()
			: null;

	private sealed record EmptySessionMessage;

	private sealed record SessionSyncRequest;

	private sealed record SessionSyncResult(bool Ok);

	private sealed record CommandWireResult(
		bool Ok,
		string? Message,
		string? Error,
		JsonElement? Data);

	private sealed record CommandRequest(string Id, JsonElement? Args);

	private sealed record EditorSessionMessage(JsonElement Session);

	private sealed record EditorFlushResult(JsonElement Session);

	private sealed record FilePathMessage(string Path);

	private sealed record DiffAgainstMessage(string Reference);

	private sealed record PullRequestQuery(string Query);

	private sealed record OpenTargetMessage(string Url);

	private sealed record SaveSourceTokenMessage(string SourceId, string Token);

	private sealed record SourceEditMessage(string Target, string OldText, string NewText);

	private sealed class BoundSessionHost : ISessionHost {
		private readonly HostCore _core;
		private readonly HostSession _source;

		public BoundSessionHost(HostCore core, HostSession source) {
			_core = core;
			_source = source;
		}

		public Task<CommandResult> NewSessionAsync(NewSessionRequest request, CancellationToken ct) =>
			_core.NewSessionAsync(_source, request, ct);

		public Task<CommandResult> ForkSessionAsync(ForkSessionRequest request, CancellationToken ct) =>
			_core.ForkSessionAsync(_source, request, ct);

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
