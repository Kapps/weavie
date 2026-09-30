using System.Text;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Inference;
using Weavie.Core.Json;
using Weavie.Core.Mcp;

namespace Weavie.AgentClientProtocol;

/// <summary>
/// One read-only consult turn on a transient ACP process: no filesystem, terminal, or MCP servers are offered, every
/// permission request is rejected, and a reported file mutation cancels the turn. See docs/specs/agent-consultation.md.
/// </summary>
internal sealed class AcpConsultClient {
	private readonly AcpTransientConnection _connection;
	private readonly Lock _gate = new();
	private readonly StringBuilder _reply = new();
	private readonly List<string> _denied = [];
	private readonly SortedSet<string> _mutated = new(StringComparer.Ordinal);
	private volatile string _sessionId = string.Empty;

	private AcpConsultClient(AcpTransientConnection connection) {
		_connection = connection;
		connection.Listen(OnRequest, OnNotification);
	}

	private string Name => _connection.Definition.Name;

	/// <summary>Opens one throwaway session and returns the controls it advertises, or throws why it couldn't.</summary>
	public static async Task<IReadOnlyList<AgentControlAxis>> ProbeAsync(AcpAgentDefinition definition, CancellationToken ct) {
		string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		await using var connection = AcpTransientConnection.Start(definition, home);
		try {
			var setup = await new AcpConsultClient(connection).OpenSessionAsync(home, ct).ConfigureAwait(false);
			return AcpConfigurationOptions.ReadIfPresent(setup);
		} catch (AcpAuthenticationRequiredException) {
			throw new InvalidOperationException(connection.AuthenticationRequired);
		} catch (Exception ex) when (ex is IOException or AcpProtocolException) {
			throw await connection.FailureAsync($"{definition.Name} didn't open a session", ex).ConfigureAwait(false);
		}
	}

	/// <summary>Runs one consult turn; every non-cancellation failure is returned as a value.</summary>
	public static async Task<AgentConsultOutcome> ConsultAsync(
		AcpAgentDefinition definition,
		AgentConsultRequest request,
		CancellationToken ct) {
		AcpTransientConnection connection;
		try {
			connection = AcpTransientConnection.Start(definition, request.Workspace);
		} catch (Exception ex) when (AcpTransientConnection.IsStartFailure(ex)) {
			return Failure(request.Model, [], $"{definition.Name} could not be started: {ex.Message}");
		}

		await using (connection) {
			return await new AcpConsultClient(connection).RunAsync(request, ct).ConfigureAwait(false);
		}
	}

	private async Task<JsonElement> OpenSessionAsync(string workspace, CancellationToken ct) {
		await _connection.RequestAsync("initialize", AcpInferenceClient.InitializeParameters, ct).ConfigureAwait(false);
		var setup = await _connection.RequestAsync("session/new", new {
			cwd = Path.GetFullPath(workspace),
			mcpServers = Array.Empty<object>(),
		}, ct).ConfigureAwait(false);
		_sessionId = AcpInferenceClient.RequiredString(setup, "sessionId");
		return setup;
	}

	private async Task<AgentConsultOutcome> RunAsync(AgentConsultRequest request, CancellationToken ct) {
		IReadOnlyList<AgentControlAxis> controls = [];
		try {
			var setup = await OpenSessionAsync(request.Workspace, ct).ConfigureAwait(false);
			controls = AcpConfigurationOptions.ReadIfPresent(setup);
			if (request.Model.Length > 0) {
				try {
					controls = await AcpProfile.ApplyAsync(_connection, _sessionId, setup, new InferenceProviderProfile {
						Model = request.Model,
						Effort = string.Empty,
						FastMode = InferenceFastMode.Inherit,
					}, ct).ConfigureAwait(false);
				} catch (AcpInferenceProfileException ex) {
					return Failure(request.Model, controls, $"{ex.Message} {AdvertisedModels(controls)}");
				}
			}

			var turn = await _connection.RequestAsync("session/prompt", new {
				sessionId = _sessionId,
				prompt = new[] { new { type = "text", text = EmbeddedAgentGuidance.ConsultInstructions + "\n\n" + request.Prompt } },
			}, ct).ConfigureAwait(false);
			return Complete(AcpInferenceClient.RequiredString(turn, "stopReason"), Model(controls, request.Model), controls);
		} catch (OperationCanceledException) {
			throw;
		} catch (AcpAuthenticationRequiredException) {
			return Failure(request.Model, controls, _connection.AuthenticationRequired);
		} catch (Exception ex) when (ex is AcpProtocolException or IOException or InvalidOperationException) {
			return Failure(request.Model, controls, $"{Name} did not complete the consult: {ex.Message}");
		}
	}

	private AgentConsultOutcome Complete(string stopReason, string model, IReadOnlyList<AgentControlAxis> controls) {
		lock (_gate) {
			if (_mutated.Count > 0) {
				return Failure(model, controls,
					$"{Name} tried to change files ({string.Join(", ", _mutated)}); consults are read-only, so Weavie stopped it.");
			}
			if (stopReason != "end_turn") return Failure(model, controls, $"{Name} stopped with '{stopReason}' before replying.");
			string reply = _reply.ToString().Trim();
			return reply.Length == 0
				? Failure(model, controls, $"{Name} returned no reply.")
				: new AgentConsultSuccess { ModelId = model, Controls = controls, Reply = reply, Denied = [.. _denied] };
		}
	}

	private void OnRequest(long id, string method, JsonElement parameters) {
		if (method != "session/request_permission" || parameters.ValueKind != JsonValueKind.Object) {
			_ = _connection.RefuseAsync(id, method);
			return;
		}

		string title = parameters.TryGetProperty("toolCall", out var tool) ? tool.GetStringOrEmpty("title") : string.Empty;
		lock (_gate) _denied.Add(title.Length > 0 ? title : "an unnamed action");
		string? reject = parameters.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
			? options.EnumerateArray()
				.OrderBy(option => option.GetStringOrNull("kind") == "reject_once" ? 0 : 1)
				.FirstOrDefault(option => option.GetStringOrNull("kind") is "reject_once" or "reject_always")
				.GetStringOrNull("optionId")
			: null;
		object outcome = reject is null ? new { outcome = "cancelled" } : new { outcome = "selected", optionId = reject };
		_ = _connection.RespondAsync(id, new { outcome });
	}

	private void OnNotification(string method, JsonElement parameters) {
		if (method != "session/update" || parameters.ValueKind != JsonValueKind.Object
			|| !parameters.TryGetProperty("update", out var update)) {
			return;
		}
		switch (update.GetStringOrNull("sessionUpdate")) {
			case "agent_message_chunk" when update.TryGetProperty("content", out var content):
				lock (_gate) _reply.Append(content.GetStringOrEmpty("text"));
				break;
			case "tool_call":
				// The reply is the agent's final message, so narration before a tool call is dropped.
				lock (_gate) _reply.Clear();
				StopOnMutation(update);
				break;
			case "tool_call_update":
				StopOnMutation(update);
				break;
		}
	}

	private void StopOnMutation(JsonElement tool) {
		if (!AcpAgentSession.MutatesFiles(tool.GetStringOrNull("kind"))) return;
		var paths = new[] { "locations", "content" }
			.Where(property => tool.TryGetProperty(property, out var items) && items.ValueKind == JsonValueKind.Array)
			.SelectMany(property => tool.GetProperty(property).EnumerateArray())
			.Select(item => item.GetStringOrNull("path"))
			.OfType<string>()
			.DefaultIfEmpty(tool.GetStringOrNull("title") is { Length: > 0 } title ? title : "an unnamed file")
			.Where(path => path.Length > 0);
		bool first;
		lock (_gate) {
			first = _mutated.Count == 0;
			_mutated.UnionWith(paths);
		}
		if (first) _ = _connection.NotifyAsync("session/cancel", new { sessionId = _sessionId });
	}

	private static string Model(IReadOnlyList<AgentControlAxis> controls, string requested) =>
		controls.FirstOrDefault(control => control.Category == "model")?.Value ?? requested;

	private static string AdvertisedModels(IReadOnlyList<AgentControlAxis> controls) =>
		controls.FirstOrDefault(control => control.Category == "model") is { } model
			? "Advertised models: " + string.Join(", ", model.Options.Select(option => $"{option.Id} ({option.Label})")) + "."
			: "It advertises no model selector.";

	private static AgentConsultFailure Failure(string model, IReadOnlyList<AgentControlAxis> controls, string detail) =>
		new() { ModelId = model, Controls = controls, Detail = detail };
}
