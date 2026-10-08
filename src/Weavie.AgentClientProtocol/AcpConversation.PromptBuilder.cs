using System.Text.Json.Nodes;
using Weavie.Core.Agents;
using Weavie.Core.Mcp;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	private PreparedPrompt BuildPrompt(AgentTurnSubmission submission) {
		if (submission.Kind == AgentTurnSubmissionKind.ProviderCommand) {
			lock (_gate) {
				var command = ResolveProviderCommandLocked(submission.CommandName);
				string text = CanonicalCommandText(submission.Text, command.Name);
				return new(new JsonArray(AcpContent.Text(text)), []);
			}
		}

		var blocks = new List<JsonNode>();
		var images = new List<SubmittedImage>();
		bool includesGuidance = false;
		if (submission.Kind == AgentTurnSubmissionKind.McpPrompt) {
			var prompt = McpPromptCatalog.Require(submission.CommandName);
			blocks.Add(AcpContent.Text(prompt.Text));
			string details = submission.Text[(prompt.Name.Length + 1)..].Trim();
			if (details.Length > 0) blocks.Add(AcpContent.Text(details));
		} else if (submission.Text.Length > 0) {
			blocks.Add(AcpContent.Text(submission.Text));
		}
		foreach (var attachment in submission.Attachments) {
			if (!_features.Images) {
				throw new AcpProtocolException($"{Definition.Name} does not accept image prompts.");
			}
			var image = new SubmittedImage(attachment.Id, attachment.Mime,
				Convert.ToBase64String(_context.FileSystem.ReadAllBytes(attachment.Path)));
			images.Add(image);
			blocks.Add(AcpContent.Image(image.MediaType, image.Data));
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

		if (_spec.SideScoped) {
			blocks.Add(AcpContent.AssistantText(EmbeddedAgentGuidance.SideConversationInstructions));
		}

		return new([.. blocks], images);
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
