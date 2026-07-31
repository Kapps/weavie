using System.Text;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Editor;

namespace Weavie.Hosting;

public sealed partial class HostSession {
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
		files.Handle<FileWriteMessage, FileWriteResult>(
			"write",
			(message, _) => {
				var result = FileProvider.Write(message.Path, message.Content);
				if (result.Ok) {
					Changes.RecordHandEdit(message.Path, message.Content);
				}

				return Task.FromResult(result);
			});
		files.Handle<FilePathMessage>("listDirectory", (message, _) => {
			ListDirectory(message.Path);
			return Task.CompletedTask;
		});
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

		if (!PastedImageMedia.TryExtension(message.Mime, out string extension)) {
			Notify($"Can't paste that image type ({(message.Mime.Length == 0 ? "unknown" : message.Mime)}) — use PNG, JPEG, GIF, or WebP.");
			return;
		}

		byte[] bytes;
		try {
			bytes = DecodeImage(message.DataB64);
		} catch (InvalidOperationException ex) {
			Notify(ex.Message + " Resize it and paste again.");
			return;
		}

		if (bytes.Length > 0) {
			SendAgentImagePath(PastedImages.Write(extension, bytes));
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

			if (!PastedImageMedia.TryExtension(message.Mime, out string extension)) {
				throw new InvalidOperationException(
					$"Can't paste that image type ({(message.Mime.Length == 0 ? "unknown" : message.Mime)}) — use PNG, JPEG, GIF, or WebP.");
			}

			byte[] bytes = DecodeImage(message.DataB64);
			if (bytes.Length == 0) {
				throw new InvalidOperationException("The pasted image was empty.");
			}

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

	private static byte[] DecodeImage(string dataB64) {
		long approximateBytes = (long)dataB64.Length / 4 * 3;
		if (approximateBytes > PastedImageMedia.MaxBytes) {
			throw new InvalidOperationException(
				$"That image is {approximateBytes / (1024.0 * 1024.0):0.0} MB — Weavie accepts agent images up to {PastedImageMedia.MaxBytes / (1024 * 1024)} MB.");
		}

		return Convert.FromBase64String(dataB64);
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
