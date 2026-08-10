using System.Text;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Changes;
using Weavie.Core.Editor;
using Weavie.Hosting.Agents;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostSession {
	private readonly OrderedAfterResponse _fileSaveCompletions = new();

	private void WireMessages(Func<bool> inputFrozen, Action<int, int> shellResized) {
		WireTerminalMessages(Bus.Feature("terminal.shell"), Shell, inputFrozen, shellResized);
		if (Claude is { } agentTerminal) {
			WireTerminalMessages(
				Bus.Feature("terminal.agent"),
				agentTerminal,
				inputFrozen,
				static (_, _) => { });
			Bus.Feature("terminal.agent").Handle<ImagePasteMessage>(
				"pasteImage",
				(message, _) => {
					HandleTerminalImagePaste(message, inputFrozen);
					return Task.CompletedTask;
				});
		}

		var lsp = Bus.Feature("lsp");
		lsp.HandleOwned<LspStartMessage, LspStartResult>(
			"start",
			(message, peer, _) => Task.FromResult(
				Lsp.Start(peer, message.Server, message.Channel, out string? error)
					? new LspStartResult(true, null)
					: new LspStartResult(false, error)));
		lsp.HandleOwned<LspDataMessage>("data", (message, peer, _) => {
			Lsp.Data(peer, message.Channel, Encoding.UTF8.GetBytes(message.Payload.GetRawText()));
			return Task.CompletedTask;
		});
		lsp.HandleOwned<ChannelMessage>("stop", (message, peer, _) => Lsp.StopAsync(peer, message.Channel));
		lsp.HandleOwned<LspResetMessage>(
			"reset",
			(message, peer, _) => Lsp.DropOtherEpochsAsync(peer, message.Epoch));

		var files = Bus.Feature("files");
		files.Handle<FilePathMessage, Weavie.Core.FileSystem.FileStat>(
			"stat",
			(message, _) => Task.FromResult(FileProvider.Stat(message.Path)));
		files.Handle<FilePathMessage, FileReadResult>(
			"read",
			(message, _) => Task.FromResult(FileProvider.Read(message.Path)));
		files.HandleAfterResponse<FileWriteMessage, FileWriteResult>(
			"write",
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
		files.Handle<FilePathMessage, DirectoryListingMessage>(
			"listDirectory",
			(message, _) => Task.FromResult(ListDirectory(message.Path)));
		files.Handle<RevealFileMessage>(
			"reveal",
			(message, ct) => FileOpener.OpenAsync(
				message.Path,
				message.Line,
				message.Preview,
				scratch: false,
				ct));

		var editor = Bus.Feature("editor");
		editor.HandleOwned<JsonElement>(
			"activeChanged",
			View.IsBound,
			(message, _, _) => {
				UpdateActiveEditor(message);
				return Task.CompletedTask;
			});
		editor.HandleOwned<JsonElement>(
			"openEditorsChanged",
			View.IsBound,
			(message, _, _) => {
				UpdateOpenEditors(message);
				return Task.CompletedTask;
			});
		editor.Handle<DiffResolutionMessage, bool>(
			"resolveDiff",
			(message, _) => Task.FromResult(
				DiffPresenter.Resolve(message.Id, message.Kept, message.FinalContents)));
		WireAgentMessages(Bus.Feature("agent"), inputFrozen);
	}

	private static void WireTerminalMessages(
		Messaging.MessageFeatureChannel messages,
		TerminalController terminal,
		Func<bool> inputFrozen,
		Action<int, int> resized) {
		messages.Handle<TerminalInputMessage>("input", (message, _) => {
			if (!inputFrozen()) {
				terminal.Write(Convert.FromBase64String(message.DataB64));
			}

			return Task.CompletedTask;
		});
		messages.Handle<TerminalSizeMessage>("resize", (message, _) => {
			terminal.Resize(message.Columns, message.Rows);
			resized(message.Columns, message.Rows);
			return Task.CompletedTask;
		});
		messages.HandleOwned<TerminalSizeMessage>("ready", (message, peer, _) => {
			terminal.OnReady(messages.Target(peer), message.Columns, message.Rows);
			return Task.CompletedTask;
		});
		messages.Handle<TerminalCwdMessage>("cwd", (message, _) => {
			terminal.OnCwdReported(message.Cwd);
			return Task.CompletedTask;
		});
	}

	private void WireAgentMessages(
		Messaging.MessageFeatureChannel messages,
		Func<bool> inputFrozen) {
		messages.HandleOwned<AgentPaneHistoryRequest, object>(
			"historyPage",
			async (message, peer, ct) => AgentPaneProtocol.HistoryPage(
				await Agent.ReadHistoryPageAsync(message, peer, ct).ConfigureAwait(false)));
		messages.HandleOwned<AgentPaneHistoryClose>("historyClose", (message, peer, _) => {
			Agent.ReleaseHistoryReader(peer, message.ReadId);
			return Task.CompletedTask;
		});
		messages.Handle<EmptyMessage>("interrupt", (_, _) => {
			Agent.Structured?.Interrupt();
			return Task.CompletedTask;
		});
		messages.Handle<AgentControlMessage>("setControl", (message, _) => {
			Agent.Controls?.SetControl(message.Axis, message.Value);
			return Task.CompletedTask;
		});
		messages.Handle<AgentDecisionMessage>("approval", (message, _) => {
			Agent.Structured?.ResolveApproval(message.RequestId, message.Decision);
			return Task.CompletedTask;
		});
		messages.Handle<AgentInputMessage>("input", (message, _) => {
			Agent.Structured?.ResolveInput(
				message.RequestId,
				message.Answers.ToDictionary(
					entry => entry.Key,
					entry => (IReadOnlyList<string>)entry.Value,
					StringComparer.Ordinal));
			return Task.CompletedTask;
		});
		messages.Handle<AttachmentUploadMessage>("uploadAttachment", (message, _) => {
			HandleAttachmentUpload(message, inputFrozen);
			return Task.CompletedTask;
		});
		messages.Handle<AttachmentMessage>("removeAttachment", (message, _) => {
			AgentAttachments.Remove(message.Id);
			PublishAttachmentState(message.Id, "removed", string.Empty);
			return Task.CompletedTask;
		});
		messages.Handle<AgentSubmitMessage>("submit", (message, _) => {
			HandleAgentSubmit(message, inputFrozen);
			return Task.CompletedTask;
		});
		messages.Handle<OpenPlanMessage, bool>(
			"openPlan",
			(message, _) => Task.FromResult(
				OpenAgentPlan(message.ThreadId, message.TurnId, message.ItemId)));
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

	private void HandleAgentSubmit(AgentSubmitMessage message, Func<bool> inputFrozen) {
		string[] attachmentIds = message.AttachmentIds ?? [];
		try {
			if (inputFrozen()) {
				throw new InvalidOperationException("Agent input is paused while Weavie restarts.");
			}

			if (Agent.Structured is not { } agent) {
				throw new InvalidOperationException("This session does not use a structured agent.");
			}

			if (message.Id.Length > 0 && AgentAttachments.TryReceipt(message.Id, out var receipt)) {
				PublishSubmissionState(message.Id, receipt, "accepted", string.Empty);
				return;
			}

			string[] skills = message.Skills ?? [];
			if (message.Prompt.Trim().Length == 0 && attachmentIds.Length == 0 && skills.Length == 0) {
				throw new InvalidOperationException("Write a prompt, attach an image, or add a skill before running the agent.");
			}

			var resolved = AgentAttachments.Resolve(attachmentIds);
			agent.Submit(new AgentTurnSubmission {
				Id = message.Id,
				Text = message.Prompt,
				Attachments = resolved,
				Skills = skills,
			});
			if (message.Id.Length > 0) {
				AgentAttachments.CommitSubmission(message.Id, attachmentIds);
			}

			PublishSubmissionState(message.Id, attachmentIds, "accepted", string.Empty);
		} catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) {
			PublishSubmissionState(message.Id, [], "rejected", ex.Message);
		}
	}

	private void PublishAttachmentState(string id, string status, string error) =>
		Bus.Feature("agent").Publish("attachmentState", new { id, status, error });

	private void PublishSubmissionState(
		string id,
		IReadOnlyList<string> attachmentIds,
		string status,
		string error) =>
		Bus.Feature("agent").Publish("submissionState", new { id, attachmentIds, status, error });

	private void Notify(string message) =>
		_notificationMessages.Publish("show", new { level = "warn", message });

	private sealed record EmptyMessage;

	private sealed record TerminalInputMessage(string DataB64);

	private sealed record TerminalSizeMessage(int Columns, int Rows);

	private sealed record TerminalCwdMessage(string Cwd);

	private sealed record ImagePasteMessage(string Mime, string DataB64);

	private sealed record LspStartMessage(string Server, string Channel);

	private sealed record LspStartResult(bool Ok, string? Error);

	private sealed record LspDataMessage(string Channel, JsonElement Payload);

	private sealed record ChannelMessage(string Channel);

	private sealed record LspResetMessage(string Epoch);

	private sealed record FilePathMessage(string Path);

	private sealed record DirectoryEntryMessage(string Name, string Path, bool IsDir);

	private sealed record DirectoryListingMessage(IReadOnlyList<DirectoryEntryMessage> Entries);

	private sealed record FileWriteMessage(string Path, string Content);

	private sealed record RevealFileMessage(string Path, int Line, bool Preview);

	private sealed record DiffResolutionMessage(string Id, bool Kept, string? FinalContents);

	private sealed record OpenPlanMessage(string ThreadId, string TurnId, string ItemId);

	private sealed record AgentControlMessage(string Axis, string Value);

	private sealed record AgentDecisionMessage(string RequestId, string Decision);

	private sealed record AgentInputMessage(string RequestId, Dictionary<string, string[]> Answers);

	private sealed record AttachmentMessage(string Id);

	private sealed record AttachmentUploadMessage(string Id, string Mime, string DataB64);

	private sealed record AgentSubmitMessage(
		string Id,
		string Prompt,
		string[]? AttachmentIds,
		string[]? Skills);
}
