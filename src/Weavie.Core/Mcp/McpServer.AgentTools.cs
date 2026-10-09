using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Json;

namespace Weavie.Core.Mcp;

// listAgents / consultAgent: let the embedded agent consult another configured agent. See docs/specs/agent-consultation.md.
public sealed partial class McpServer {
	private string BuildAgentRosterJson() => JsonWrite.Object(writer => {
		writer.WriteStartArray("agents");
		foreach (var entry in _agents.Roster()) {
			writer.WriteStartObject();
			writer.WriteString("provider", entry.Provider.Id);
			writer.WriteString("name", entry.Provider.Name);
			writer.WriteBoolean("consultable", entry.Unconsultable is null);
			if (entry.Unconsultable is { } reason) writer.WriteString("reason", reason);
			if (entry.Models is { } models) WriteModels(writer, models);
			writer.WriteEndObject();
		}
		writer.WriteEndArray();
	});

	private static void WriteModels(Utf8JsonWriter writer, AgentModelEntry entry) {
		writer.WriteStartObject("models");
		writer.WriteString("state", entry.Status.ToString().ToLowerInvariant());
		writer.WriteString("since", entry.At);
		if (entry.Status == AgentModelStatus.Ready) writer.WriteString("source", entry.Source.ToString().ToLowerInvariant());
		if (entry.Error is { } error) writer.WriteString("error", error);
		writer.WriteStartArray("options");
		foreach (var option in entry.Models) {
			writer.WriteStartObject();
			writer.WriteString("id", option.Id);
			writer.WriteString("label", option.Label);
			if (option.Description is { } description) writer.WriteString("description", description);
			writer.WriteEndObject();
		}
		writer.WriteEndArray();
		writer.WriteEndObject();
	}

	private async Task HandleConsultAgentAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		string provider = args.GetStringOrEmpty("provider");
		string prompt = args.GetStringOrEmpty("prompt");
		if (provider.Length == 0 || prompt.Trim().Length == 0) {
			await SendToolErrorAsync(responder, idRaw, "consultAgent requires a 'provider' and a 'prompt'.", ct).ConfigureAwait(false);
			return;
		}

		var outcome = await _agents.ConsultAsync(provider, new AgentConsultRequest {
			Workspace = PrimaryWorkspaceRoot,
			Model = args.GetStringOrEmpty("model"),
			Prompt = prompt,
		}, ct).ConfigureAwait(false);
		string model = outcome.ModelId.Length > 0 ? outcome.ModelId : "default model";
		await (outcome switch {
			AgentConsultSuccess success => SendToolTextAsync(responder, idRaw,
				$"{provider} ({model}) replied:\n\n{success.Reply}"
					+ (success.Denied.Count > 0 ? $"\n\n(Weavie denied: {string.Join("; ", success.Denied)})" : string.Empty),
				ct),
			AgentConsultFailure failure => SendToolErrorAsync(responder, idRaw, failure.Detail, ct),
			_ => throw new InvalidOperationException($"Unknown consult outcome {outcome.GetType().Name}."),
		}).ConfigureAwait(false);
	}
}
