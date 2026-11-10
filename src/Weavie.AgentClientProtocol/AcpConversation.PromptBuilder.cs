using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Mcp;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	private PreparedPrompt BuildPrompt(AgentTurnSubmission submission) {
		var blocks = new List<JsonNode>();
		var images = new List<SubmittedImage>();
		if (submission.Kind == AgentTurnSubmissionKind.ProviderCommand) {
			lock (_gate) {
				blocks.Add(AcpContent.Text(CanonicalCommandText(submission.Text, ResolveProviderCommandLocked(submission.CommandName).Name)));
			}
			// The command stays the first block, which is how agents recognize it; images and context may follow it.
			AddImages(submission, blocks, images);
			if (submission.CarriesTurnContext && _context.TurnContext.Compose() is { Length: > 0 } stage) {
				blocks.Add(AcpContent.AssistantText(stage));
			}
			return new([.. blocks], images);
		}

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

		if (_spec.Side) {
			blocks.Add(AcpContent.AssistantText(EmbeddedAgentGuidance.SideConversationInstructions));
		} else if (_context.TurnContext.Compose() is { Length: > 0 } turnContext) {
			blocks.Add(AcpContent.AssistantText(turnContext));
		}

		return new([.. blocks], images);
	}

	private void AddImages(AgentTurnSubmission submission, List<JsonNode> blocks, List<SubmittedImage> images) {
		foreach (var attachment in submission.Attachments) {
			if (!_features.Images) {
				throw new AcpProtocolException($"{Definition.Name} does not accept image prompts.");
			}
			var image = new SubmittedImage(attachment.Id, attachment.Mime,
				Convert.ToBase64String(_context.FileSystem.ReadAllBytes(attachment.Path)));
			images.Add(image);
			blocks.Add(AcpContent.Image(image.MediaType, image.Data));
		}
	}

	private void EmitSubmitted(AgentTurnSubmission submission, string type, IReadOnlyList<SubmittedImage> images) {
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
