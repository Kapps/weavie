using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Mcp;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	private PreparedPrompt BuildPrompt(AgentTurnSubmission submission) {
		var blocks = new List<JsonNode>();
		var images = new List<SubmittedImage>();
		JsonObject? command = null;
		if (submission.Kind == AgentTurnSubmissionKind.ProviderCommand) {
			lock (_gate) {
				command = AcpContent.Text(CanonicalCommandText(submission.Text, ResolveProviderCommandLocked(submission.CommandName).Name));
			}
			blocks.Add(command);
			AddImages(submission, blocks, images);
		} else {
			AddPromptBlocks(submission, blocks, images);
		}
		if (_spec.Role == AcpConversationRole.Side) {
			blocks.Add(AcpContent.AssistantText(EmbeddedAgentGuidance.SideConversationInstructions));
		} else if ((command is null || submission.CarriesTurnContext) && _binding.TurnContext.Compose() is { Length: > 0 } turnContext) {
			blocks.Add(AcpContent.AssistantText(turnContext));
		}
		// Agents read a command from the first block or from the last text block, so one with company is sent as both.
		if (command is not null && blocks.Count > 1) blocks.Add(command.DeepClone());
		return new([.. blocks], images);
	}

	private void AddPromptBlocks(AgentTurnSubmission submission, List<JsonNode> blocks, List<SubmittedImage> images) {
		if (submission.Sender is { } sender) blocks.Add(AcpContent.AssistantText(EmbeddedAgentGuidance.SessionMessage(sender)));
		bool includesGuidance = false;
		if (submission.Kind == AgentTurnSubmissionKind.McpPrompt) {
			var prompt = McpPromptCatalog.Require(submission.CommandName);
			blocks.Add(AcpContent.Text(prompt.Text));
			string details = submission.Text[(prompt.Name.Length + 1)..].Trim();
			if (details.Length > 0) blocks.Add(AcpContent.Text(details));
		} else if (submission.Text.Length > 0) {
			blocks.Add(AcpContent.Text(submission.Text));
		}
		AddImages(submission, blocks, images);
		foreach (var selection in submission.Attachments.OfType<AgentInputAttachment.Selection>()) {
			blocks.Add(AcpContent.Text($"Attached selection ({selection.Label}):\n{selection.Text}"));
		}

		if (_features.EmbeddedContext) {
			lock (_gate) includesGuidance = !_guidanceSent;
			if (includesGuidance) {
				blocks.Add(AcpContent.AssistantResource(
					"weavie://instructions",
					EmbeddedAgentGuidance.Compose(_context.Runtime)));
			}
			if (_context.Editor.Active is { } editor) {
				string selection = $"Active file: {editor.FilePath}\n"
					+ $"Language: {editor.LanguageId ?? "unknown"}\n"
					+ $"Selection: {editor.Selection.Start.Line + 1}:{editor.Selection.Start.Character + 1}"
					+ $"-{editor.Selection.End.Line + 1}:{editor.Selection.End.Character + 1}\n"
					+ editor.SelectedText;
				string path = Path.GetFullPath(editor.FilePath);
				string uri = new UriBuilder(Uri.UriSchemeFile, string.Empty) { Path = path }.Uri.AbsoluteUri;
				blocks.Add(AcpContent.AssistantResource(uri + "#selection", selection));
			}
		}
		if (includesGuidance) {
			lock (_gate) _guidanceSent = true;
		}
	}

	private void RequireImageSupport(AgentTurnSubmission submission) {
		if (submission.Attachments.OfType<AgentInputAttachment.Image>().Any() && !_features.Images) {
			throw new AcpProtocolException($"{Definition.Name} does not accept image prompts.");
		}
	}

	private void AddImages(AgentTurnSubmission submission, List<JsonNode> blocks, List<SubmittedImage> images) {
		RequireImageSupport(submission);
		foreach (var attachment in submission.Attachments.OfType<AgentInputAttachment.Image>()) {
			var image = new SubmittedImage(attachment.Id, attachment.Mime,
				Convert.ToBase64String(_context.FileSystem.ReadAllBytes(attachment.Path)));
			images.Add(image);
			blocks.Add(AcpContent.Image(image.MediaType, image.Data));
		}
	}

	private void EmitSubmitted(AgentTurnSubmission submission, string type, IReadOnlyList<SubmittedImage> images) {
		CompleteContentStreams();
		if (submission.Text.Length > 0) {
			Emit(new AgentPaneMessage {
				Type = type,
				ProviderId = Definition.Id,
				ThreadId = SessionId(),
				TurnId = TurnId(),
				ItemId = submission.Id,
				Text = submission.Text,
				Sender = submission.Sender,
			});
		}
		foreach (var selection in submission.Attachments.OfType<AgentInputAttachment.Selection>()) {
			Emit(new AgentPaneMessage {
				Type = "user-selection",
				ProviderId = Definition.Id,
				ThreadId = SessionId(),
				TurnId = TurnId(),
				ItemId = selection.Id,
				Summary = selection.Label,
				Text = selection.Text,
				Status = "submitted",
			});
		}
		foreach (var image in images) {
			Emit(new AgentPaneMessage {
				Type = "user-image",
				ProviderId = Definition.Id,
				ThreadId = SessionId(),
				TurnId = TurnId(),
				ItemId = image.Id,
				MediaType = image.MediaType,
				MediaData = image.Data,
				Status = "submitted",
			});
		}
	}

	private sealed record SubmittedImage(string Id, string MediaType, string Data);
	private sealed record PreparedPrompt(JsonArray Blocks, IReadOnlyList<SubmittedImage> Images);
}
