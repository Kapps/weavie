using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.Editor;
using Weavie.Hosting.Agents;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostSession {
	/// <summary>The Connect Service… prompt awaiting this session's pasted key; it ends with the session.</summary>
	internal PendingReply<PendingConnect> ConnectPrompt { get; } = new();

	/// <summary>The host-driven picker awaiting this session's choice; it ends with the session.</summary>
	internal PendingReply<PendingPick> OpenPicker { get; } = new();

	private readonly OrderedAfterResponse _fileSaveCompletions = new();
	private readonly ConditionalWeakTable<MessagePeer, DirectoryWatchSubscriptions> _directoryWatchOwners = [];

	private DirectoryWatchSubscriptions DirectoryWatches(MessagePeer peer) =>
		_directoryWatchOwners.GetValue(peer, _ => new DirectoryWatchSubscriptions(ObservedPaths));

	private void WireMessages(
		Func<bool> inputFrozen,
		Action<bool, Action> acceptTerminalInput) {
		if (Claude is { } agentTerminal) {
			_ = TerminalMessageWiring.Wire(
				Bus.Feature("terminal.agent"),
				agentTerminal,
				acceptTerminalInput,
				static (_, _) => { });
			Bus.Feature("terminal.agent").Handle(
				"pasteImage", MessageActivity.ActionFailed("Pasting an image into the agent"), WireJson.Default.ImagePasteMessage,
				(message, _) => {
					HandleTerminalImagePaste(message, inputFrozen);
					return Task.CompletedTask;
				});
		} else if (Agent.AuthenticationTerminal is { } authenticationTerminal) {
			_ = TerminalMessageWiring.Wire(
				Bus.Feature("terminal.agent"),
				authenticationTerminal.Controller,
				acceptTerminalInput,
				static (_, _) => { });
		}

		var lsp = Bus.Feature("lsp");
		lsp.HandleOwned(
			"start", MessageActivity.ActionFailed("Starting a language server"), WireJson.Default.LspStartMessage, WireJson.Default.LspStartResult,
			(message, peer, _) => Task.FromResult(
				Lsp.Start(peer, message.Server, message.Channel, out string? error)
					? new LspStartResult(true, null)
					: new LspStartResult(false, error)));
		lsp.HandleOwned("data", MessageActivity.ActionFailed("Talking to a language server"), WireJson.Default.LspDataMessage, (message, peer, _) => {
			Lsp.Data(peer, message.Channel, Encoding.UTF8.GetBytes(message.Payload.GetRawText()));
			return Task.CompletedTask;
		});
		lsp.HandleOwned("stop", MessageActivity.ActionFailed("Stopping a language server"), WireJson.Default.ChannelMessage, (message, peer, _) => Lsp.StopAsync(peer, message.Channel));
		lsp.HandleOwned(
			"reset", MessageActivity.ActionFailed("Resetting language servers"), WireJson.Default.LspResetMessage,
			(message, peer, _) => Lsp.DropOtherEpochsAsync(peer, message.Epoch));

		var files = Bus.Feature("files");
		files.Handle(
			"stat", MessageActivity.ActionFailed("Checking a file"), WireJson.Default.FilePathMessage, WireJson.Default.FileStat,
			(message, _) => Task.FromResult(FileProvider.Stat(message.Path)));
		files.Handle(
			"read", MessageActivity.ActionFailed("Reading a file"), WireJson.Default.FilePathMessage, WireJson.Default.FileReadResult,
			(message, _) => Task.FromResult(FileProvider.Read(message.Path)));
		files.HandleAfterResponse(
			"write", MessageActivity.SessionCritical("Saving a file"), WireJson.Default.FileWriteMessage, WireJson.Default.FileWriteResult,
			(message, _) => {
				var result = FileProvider.Write(message.Path, message.Content);
				var handEdit = result.Ok
					? Changes.CaptureHandEdit(message.Path, message.Content)
					: CapturedHandEdit.None;
				return Task.FromResult(new ResponseWithCompletion<FileWriteResult>(result, _fileSaveCompletions.Reserve(_ => {
					if (result.Ok) {
						Exception? correctionError = null;
						try {
							handEdit.Complete();
						} catch (Exception ex) {
							correctionError = ex;
						}
						FileActivity.ReportBufferSaved(message.Path, result.Stat);
						if (correctionError is not null) {
							Notify($"Couldn't record your correction: {correctionError.Message}");
							throw correctionError;
						}
					}
					return Task.CompletedTask;
				})));
			});
		Bus.PeerDisconnected += peer => DirectoryWatches(peer).Dispose();
		files.HandleOwned(
			"listDirectory", MessageActivity.ActionFailed("Listing a folder"), WireJson.Default.ListDirectoryMessage, WireJson.Default.DirectoryListingMessage,
			(message, peer, _) => Task.FromResult(ListDirectory(peer, message.SubscriptionId, message.Path)));
		files.HandleOwned("unwatchDirectory", MessageActivity.ActionFailed("Unwatching a folder"), WireJson.Default.UnwatchDirectoryMessage, (message, peer, _) => {
			DirectoryWatches(peer).Unwatch(message.SubscriptionId);
			return Task.CompletedTask;
		});
		files.HandleOwned("reset", MessageActivity.ActionFailed("Resetting folder watches"), WireJson.Default.ResetDirectoryWatchesMessage, (message, peer, _) => {
			DirectoryWatches(peer).Reset(message.PageEpoch);
			return Task.CompletedTask;
		});
		files.HandleConcurrent(
			"reveal", MessageActivity.ActionFailed("Revealing a file"), WireJson.Default.RevealFileMessage,
			(message, ct) => FileOpener.OpenAsync(
				message.Path,
				message.Line,
				message.Preview,
				rendered: false,
				scratch: false,
				EditorOpenIntent.Navigation,
				ct));

		var editor = Bus.Feature("editor");
		editor.HandleOwned(
			"activeChanged", MessageActivity.ActionFailed("Updating the active editor"),
			WireJson.Default.JsonElement,
			View.IsBound,
			(message, _, _) => {
				UpdateActiveEditor(message);
				return Task.CompletedTask;
			});
		editor.HandleOwned(
			"openEditorsChanged", MessageActivity.ActionFailed("Updating open editors"),
			WireJson.Default.JsonElement,
			View.IsBound,
			(message, _, _) => {
				UpdateOpenEditors(message);
				return Task.CompletedTask;
			});
		editor.Handle(
			"resolveDiff", MessageActivity.ActionFailed("Resolving a diff"), WireJson.Default.DiffResolutionMessage, WireJson.Default.Boolean,
			(message, _) => Task.FromResult(
				DiffPresenter.Resolve(message.Id, message.Kept, message.FinalContents)));
		WireAgentMessages(Bus.Feature("agent"), inputFrozen, acceptTerminalInput);
	}

	private void WireAgentMessages(
		Messaging.MessageFeatureChannel messages,
		Func<bool> inputFrozen,
		Action<bool, Action> acceptInput) {
		messages.Handle("typing", MessageActivity.ActionFailed("Reporting typing to the agent"), WireJson.Default.EmptyPayload, (_, _) => {
			acceptInput(true, static () => { });
			return Task.CompletedTask;
		});
		messages.Handle("toolOutput", MessageActivity.ActionFailed("Loading tool output"), WireJson.Default.AgentPaneRecordRequest, WireJson.Default.AgentPaneWire, (message, _) =>
			Task.FromResult(AgentPaneProtocol.Message(Agent.ReadRecord(message))));
		messages.Handle("interrupt", MessageActivity.ActionFailed("Interrupting the agent"), WireJson.Default.EmptyPayload, (_, _) => {
			Agent.Structured?.Interrupt();
			return Task.CompletedTask;
		});
		messages.Handle("setControl", MessageActivity.ActionFailed("Changing an agent setting"), WireJson.Default.AgentControlMessage,
			(message, _) => SetAgentControlAsync(message.Axis, message.Value));
		messages.Handle("permission", MessageActivity.ActionFailed("Answering a permission request"), WireJson.Default.AgentDecisionMessage, (message, _) => {
			Agent.Structured?.ResolvePermission(message.RequestId, message.OptionId);
			return Task.CompletedTask;
		});
		messages.Handle("authenticate", MessageActivity.ActionFailed("Signing in to the agent"), WireJson.Default.AgentAuthMessage, (message, _) => {
			Agent.Structured?.Authenticate(
				message.RequestId,
				message.MethodId,
				message.Answers.ToDictionary(
					entry => entry.Key,
					entry => (IReadOnlyList<string>)entry.Value,
					StringComparer.Ordinal));
			return Task.CompletedTask;
		});
		messages.Handle("input", MessageActivity.ActionFailed("Sending input to the agent"), WireJson.Default.AgentInputMessage, (message, _) => {
			Agent.Structured?.ResolveInput(
				message.RequestId,
				message.Action,
				message.Answers.ToDictionary(
					entry => entry.Key,
					entry => (IReadOnlyList<string>)entry.Value,
					StringComparer.Ordinal));
			return Task.CompletedTask;
		});
		messages.Handle("uploadAttachment", MessageActivity.ActionFailed("Uploading an attachment"), WireJson.Default.AttachmentUploadMessage, (message, _) => {
			HandleAttachmentUpload(message, inputFrozen);
			return Task.CompletedTask;
		});
		messages.Handle("removeAttachment", MessageActivity.ActionFailed("Removing an attachment"), WireJson.Default.AttachmentMessage, (message, _) => {
			AgentAttachments.Remove(message.Id);
			PublishAttachmentState(message.Id, "removed", string.Empty);
			return Task.CompletedTask;
		});
		messages.Handle("submit", MessageActivity.ActionFailed("Sending your prompt to the agent"), WireJson.Default.AgentSubmitMessage, (message, _) => {
			HandleAgentSubmit(message, inputFrozen, submission => {
				var agent = Agent.Structured
					?? throw new InvalidOperationException("This session does not use a structured agent.");
				agent.Submit(submission);
			});
			return Task.CompletedTask;
		});
		messages.Handle("replyAside", MessageActivity.ActionFailed("Sending a side reply"), WireJson.Default.AgentSideReplyMessage, (message, _) => {
			HandleAgentSubmit(message.Submission, inputFrozen, submission => {
				var sideConversations = Agent.SideConversations
					?? throw new InvalidOperationException("This agent does not support side conversations.");
				sideConversations.ReplyAside(message.ConversationId, submission);
			});
			return Task.CompletedTask;
		});
		messages.Handle("setAsideCollapsed", MessageActivity.ActionFailed("Updating a side conversation"), WireJson.Default.AgentAsideCollapsedMessage, (message, _) => {
			var sideConversations = Agent.SideConversations
				?? throw new InvalidOperationException("This agent does not support side conversations.");
			sideConversations.SetAsideCollapsed(message.ConversationId, message.Collapsed);
			return Task.CompletedTask;
		});
		messages.Handle(
			"openPlan", MessageActivity.ActionFailed("Opening the plan"), WireJson.Default.OpenPlanMessage, WireJson.Default.Boolean,
			(message, _) => Task.FromResult(
				OpenAgentPlan(message.ProviderId, message.ConversationId, message.ThreadId, message.TurnId, message.ItemId)));
	}

	private void HandleTerminalImagePaste(ImagePasteMessage message, Func<bool> inputFrozen) {
		if (inputFrozen()) {
			return;
		}

		try {
			var (extension, bytes) = PastedImageMedia.Decode(message.Mime, message.DataB64);
			SendAgentImagePath(PastedImages.Write(extension, bytes));
		} catch (Exception ex) when (ex is FormatException or InvalidOperationException) {
			Notify(ex.Message + " Resize it and paste again.");
		}
	}

	private void HandleAttachmentUpload(AttachmentUploadMessage message, Func<bool> inputFrozen) {
		try {
			if (inputFrozen()) {
				throw new InvalidOperationException("Agent input is paused while Weavie restarts.");
			}

			if (Agent.Structured is null) {
				throw new InvalidOperationException("This agent does not accept structured attachments.");
			}

			var (extension, bytes) = PastedImageMedia.Decode(message.Mime, message.DataB64);

			var attachment = AgentAttachments.Add(message.Id, message.Mime, extension, bytes);
			PublishAttachmentState(message.Id, attachment is null ? "removed" : "ready", string.Empty);
		} catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException) {
			PublishAttachmentState(message.Id, "failed", ex.Message);
		}
	}

	private void HandleAgentSubmit(
		AgentSubmitMessage message,
		Func<bool> inputFrozen,
		Action<AgentTurnSubmission> submit) {
		try {
			if (inputFrozen()) {
				throw new InvalidOperationException("Agent input is paused while Weavie restarts.");
			}

			var receipt = AcceptAgentSubmission(message, submit);
			PublishSubmissionState(message.Id, receipt, "accepted", string.Empty);
		} catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
			PublishSubmissionState(message.Id, [], "rejected", ex.Message);
		}
	}

	internal IReadOnlyList<string> AcceptAgentSubmission(AgentSubmitMessage message, Action<AgentTurnSubmission> submit) {
		string[] attachmentIds = message.AttachmentIds ?? [];
		AgentInputAttachment.Selection[] selections = [.. (message.Selections ?? []).Select(Selection)];
		if (message.Prompt.Trim().Length == 0 && attachmentIds.Length == 0 && selections.Length == 0) {
			throw new InvalidOperationException("Write a prompt or attach something before running the agent.");
		}
		var kind = message.Kind switch {
			"prompt" => AgentTurnSubmissionKind.Prompt,
			"providerCommand" => AgentTurnSubmissionKind.ProviderCommand,
			"mcpPrompt" => AgentTurnSubmissionKind.McpPrompt,
			_ => throw new InvalidOperationException("Agent submissions require a recognized semantic kind."),
		};
		if (kind == AgentTurnSubmissionKind.Prompt && message.CommandName is { Length: > 0 }) {
			throw new InvalidOperationException("An ordinary prompt cannot name a provider command.");
		}
		if (kind == AgentTurnSubmissionKind.ProviderCommand && attachmentIds.Length + selections.Length != 0) {
			throw new InvalidOperationException("Provider commands cannot include attachments.");
		}
		return [.. AgentAttachments.Submit(message.Id, attachmentIds, images => submit(new AgentTurnSubmission {
			Id = message.Id,
			Text = message.Prompt,
			Kind = kind,
			CommandName = message.CommandName ?? string.Empty,
			Attachments = [.. images, .. selections],
		})), .. selections.Select(selection => selection.Id)];
	}

	private static AgentInputAttachment.Selection Selection(AgentSelectionWire wire) {
		if (wire.Id.Length == 0 || wire.Label.Trim().Length == 0 || wire.Text.Length == 0) {
			throw new InvalidOperationException("A selection attachment needs an id, a label, and text.");
		}
		if (wire.Text.Length > AgentInputAttachment.Selection.MaxTextLength) {
			throw new InvalidOperationException(
				$"The selection '{wire.Label}' has {wire.Text.Length:N0} characters; the limit is {AgentInputAttachment.Selection.MaxTextLength:N0}. Select less and attach it again.");
		}
		return new AgentInputAttachment.Selection { Id = wire.Id, Label = wire.Label.Trim(), Text = wire.Text };
	}

	private void PublishAttachmentState(string id, string status, string error) =>
		Bus.Feature("agent").Publish("attachmentState", WireJson.Default.AttachmentState, new(id, status, error));

	private void PublishSubmissionState(
		string id,
		IReadOnlyList<string> attachmentIds,
		string status,
		string error) =>
		Bus.Feature("agent").Publish("submissionState", WireJson.Default.SubmissionState, new(id, attachmentIds, status, error));

	/// <summary>Shows a toast on this session's clients.</summary>
	/// <param name="level">The toast level (<c>info</c>, <c>warn</c>, <c>error</c>).</param>
	/// <param name="message">The text shown to the user.</param>
	public void Notify(string level, string message) =>
		_notificationMessages.Publish("show", WireJson.Default.ToastMessage, ToastMessage.Plain(level, message));

	private void Notify(string message) => Notify("warn", message);

	internal sealed record ImagePasteMessage(string Mime, string DataB64);

	internal sealed record LspStartMessage(string Server, string Channel);

	internal sealed record LspStartResult(bool Ok, string? Error);

	internal sealed record LspDataMessage(string Channel, JsonElement Payload);

	internal sealed record ChannelMessage(string Channel);

	internal sealed record LspResetMessage(string Epoch);

	internal sealed record ListDirectoryMessage(string SubscriptionId, string Path);
	internal sealed record UnwatchDirectoryMessage(string SubscriptionId);
	internal sealed record ResetDirectoryWatchesMessage(string PageEpoch);

	internal sealed record DirectoryEntryMessage(string Name, string Path, bool IsDir);

	internal sealed record DirectoryListingMessage(string Path, IReadOnlyList<DirectoryEntryMessage> Entries);

	internal sealed record FileWriteMessage(string Path, string Content);

	internal sealed record RevealFileMessage(string Path, int? Line, bool Preview);

	internal sealed record DiffResolutionMessage(string Id, bool Kept, string? FinalContents);

	internal sealed record OpenPlanMessage(string ProviderId, string? ConversationId, string ThreadId, string TurnId, string ItemId);

	internal sealed record AgentControlMessage(string Axis, string Value);

	internal sealed record AgentDecisionMessage(string RequestId, string OptionId);

	internal sealed record AgentAuthMessage(
		string RequestId,
		string MethodId,
		Dictionary<string, string[]> Answers);

	internal sealed record AgentInputMessage(
		string RequestId,
		string Action,
		Dictionary<string, string[]> Answers);

	internal sealed record AttachmentMessage(string Id);

	internal sealed record AttachmentUploadMessage(string Id, string Mime, string DataB64);

	internal sealed record AgentSubmitMessage(
		string Id,
		string Prompt,
		string? Kind,
		string? CommandName,
		string[]? AttachmentIds,
		AgentSelectionWire[]? Selections);

	/// <summary>Values the user selected in the pane, attached to a prompt as text.</summary>
	internal sealed record AgentSelectionWire(string Id, string Label, string Text);

	internal sealed record AgentSideReplyMessage(string ConversationId, AgentSubmitMessage Submission);

	internal sealed record AgentAsideCollapsedMessage(string ConversationId, bool Collapsed);
}

internal sealed record AttachmentState(string Id, string Status, string Error);

internal sealed record SubmissionState(string Id, IReadOnlyList<string> AttachmentIds, string Status, string Error);
