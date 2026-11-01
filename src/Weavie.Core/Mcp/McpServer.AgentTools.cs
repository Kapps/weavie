using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Json;

namespace Weavie.Core.Mcp;

// listAgents / consultAgent / awaitConsult: let the embedded agent consult another configured agent. See docs/specs/agent-consultation.md.
public sealed partial class McpServer {
	private readonly Dictionary<string, RunningConsult> _consults = [];
	private readonly Lock _consultsGate = new();
	private readonly CancellationTokenSource _consultsLifetime = new();

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

		var slice = ConsultSlice();
		var stop = CancellationTokenSource.CreateLinkedTokenSource(_consultsLifetime.Token);
		var consult = new RunningConsult(provider, _agents.ConsultAsync(provider, new AgentConsultRequest {
			Workspace = PrimaryWorkspaceRoot,
			Model = args.GetStringOrEmpty("model"),
			Prompt = prompt,
		}, stop.Token), stop);
		string id = Guid.NewGuid().ToString("N")[..8];
		lock (_consultsGate) _consults[id] = consult;
		await AwaitConsultAsync(responder, idRaw, id, consult, slice, ct).ConfigureAwait(false);
	}

	private async Task HandleAwaitConsultAsync(IMcpResponder responder, JsonElement args, string? idRaw, CancellationToken ct) {
		string id = args.GetStringOrEmpty("consult");
		RunningConsult? consult;
		lock (_consultsGate) _consults.TryGetValue(id, out consult);
		await (consult is null
			? SendToolErrorAsync(responder, idRaw, $"No running consult '{id}'; its reply was already returned or it was cancelled.", ct)
			: AwaitConsultAsync(responder, idRaw, id, consult, ConsultSlice(), ct)).ConfigureAwait(false);
	}

	// One wait slice, shorter than callers' hard tool timeouts (Codex: 60s).
	private TimeSpan ConsultSlice() =>
		TimeSpan.FromSeconds(Require(_settings, "Settings").RequireInt(AgentSettings.ConsultWaitSeconds));

	// A slice ending returns the id to wait again; cancelling the waiting call stops the consult.
	private async Task AwaitConsultAsync(
		IMcpResponder responder, string? idRaw, string id, RunningConsult consult, TimeSpan slice, CancellationToken ct) {
		AgentConsultOutcome outcome;
		try {
			outcome = await consult.Outcome.WaitAsync(slice, ct).ConfigureAwait(false);
		} catch (TimeoutException) {
			await SendToolTextAsync(responder, idRaw,
				$"{consult.Provider} is still working (consult '{id}'). Call awaitConsult with consult '{id}' to keep waiting for its reply.",
				ct).ConfigureAwait(false);
			return;
		} catch {
			ForgetConsult(id, consult, ct.IsCancellationRequested);
			throw;
		}

		ForgetConsult(id, consult, stop: false);
		string model = outcome.ModelId.Length > 0 ? outcome.ModelId : "default model";
		await (outcome switch {
			AgentConsultSuccess success => SendToolTextAsync(responder, idRaw,
				$"{consult.Provider} ({model}) replied:\n\n{success.Reply}"
					+ (success.Denied.Count > 0 ? $"\n\n(Weavie denied: {string.Join("; ", success.Denied)})" : string.Empty),
				ct),
			AgentConsultFailure failure => SendToolErrorAsync(responder, idRaw, failure.Detail, ct),
			_ => throw new InvalidOperationException($"Unknown consult outcome {outcome.GetType().Name}."),
		}).ConfigureAwait(false);
	}

	private void ForgetConsult(string id, RunningConsult consult, bool stop) {
		lock (_consultsGate) {
			if (!_consults.Remove(id)) return;
		}
		if (stop) consult.Stop.Cancel();
		consult.Stop.Dispose();
	}

	private sealed record RunningConsult(string Provider, Task<AgentConsultOutcome> Outcome, CancellationTokenSource Stop);
}
