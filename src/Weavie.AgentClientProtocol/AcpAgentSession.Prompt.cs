using Weavie.Core.Agents;
using Weavie.Core.Mcp;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private PreparedPrompt BuildPrompt(AgentTurnSubmission submission) {
		if (submission.Kind == AgentTurnSubmissionKind.ProviderCommand) {
			lock (_gate) {
				var command = ResolveProviderCommandLocked(submission.CommandName);
				string text = CanonicalCommandText(submission.Text, command);
				return new([new { type = "text", text }], []);
			}
		}

		var blocks = new List<object>();
		var images = new List<SubmittedImage>();
		bool includesGuidance = false;
		if (submission.Text.Length > 0) {
			blocks.Add(new { type = "text", text = submission.Text });
		}
		foreach (var attachment in submission.Attachments) {
			if (!_supportsImages) {
				throw new AcpProtocolException($"{_definition.Name} does not accept image prompts.");
			}
			var image = new SubmittedImage(attachment.Id, attachment.Mime,
				Convert.ToBase64String(_context.FileSystem.ReadAllBytes(attachment.Path)));
			images.Add(image);
			blocks.Add(new {
				type = "image",
				mimeType = image.MediaType,
				data = image.Data,
			});
		}

		if (_supportsEmbeddedContext) {
			lock (_gate) includesGuidance = !_guidanceSent;
			if (includesGuidance) {
				blocks.Add(AssistantContext(
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
				blocks.Add(AssistantContext(uri + "#selection", selection));
			}
		}
		if (includesGuidance) {
			lock (_gate) _guidanceSent = true;
		}

		if (_role is SideRole) {
			blocks.Add(new {
				type = "text",
				text = EmbeddedAgentGuidance.SideConversationInstructions,
				annotations = new { audience = new[] { "assistant" } },
			});
		}

		return new([.. blocks], images);
	}

	private static object AssistantContext(string uri, string text) => new {
		type = "resource",
		annotations = new { audience = new[] { "assistant" } },
		resource = new {
			uri,
			mimeType = "text/plain",
			text,
		},
	};

	private void EmitSubmitted(AgentTurnSubmission submission, string type, IReadOnlyList<SubmittedImage> images) {
		if (submission.Text.Length > 0) {
			Emit(new AgentPaneMessage {
				Type = type,
				ProviderId = _definition.Id,
				ThreadId = SessionId(),
				TurnId = TurnId(),
				ItemId = submission.Id,
				Text = submission.Text,
			});
		}
		foreach (var image in images) {
			Emit(new AgentPaneMessage {
				Type = "user-image",
				ProviderId = _definition.Id,
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
	private sealed record PreparedPrompt(object[] Blocks, IReadOnlyList<SubmittedImage> Images);
}
