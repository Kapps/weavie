using System.Text;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Inference;

namespace Weavie.AgentClientProtocol;

/// <summary>
/// One isolated ACP query: a transient agent process, one throwaway session rooted at the owning worktree, and one
/// prompt turn. The client advertises only typed boolean configuration and refuses every agent request, so the
/// agent has no tools, filesystem, or MCP surface to reach for.
/// </summary>
internal sealed class AcpInferenceClient {
	internal static readonly object InitializeParameters = new {
		protocolVersion = 1,
		clientCapabilities = new {
			session = new { configOptions = new { boolean = new { } } },
		},
		clientInfo = new { name = "weavie", title = "Weavie", version = "1" },
	};
	private readonly AcpTransientConnection _connection;
	private readonly StringBuilder _reply = new();
	private readonly Lock _replyGate = new();
	private int _replyBytes;
	private int _maxReplyBytes = int.MaxValue;
	private volatile bool _replyOverflowed;

	private AcpInferenceClient(AcpTransientConnection connection) {
		_connection = connection;
		// Every agent request is refused: inference gets no tools, filesystem, terminal, or elicitation.
		connection.Listen((id, method, _) => connection.RefuseAsync(id, method), Collect);
	}

	/// <summary>Runs exactly one query. Never retries, restarts, or falls back to another agent.</summary>
	public static async Task<InferenceProviderResult> QueryAsync(
		AcpAgentDefinition definition,
		InferenceProviderRequest request,
		CancellationToken ct) {
		AcpTransientConnection connection;
		try {
			connection = AcpTransientConnection.Start(definition, request.Workspace, static _ => { });
		} catch (Exception ex) when (AcpTransientConnection.IsStartFailure(ex)) {
			return Failure(definition.Id, InferenceFailureKind.NotConfigured,
				$"The ACP agent '{definition.Name}' could not be started.");
		}

		await using (connection) {
			return await new AcpInferenceClient(connection).RunAsync(request, ct).ConfigureAwait(false);
		}
	}

	private async Task<InferenceProviderResult> RunAsync(InferenceProviderRequest request, CancellationToken ct) {
		string model = _connection.Definition.Id;
		_maxReplyBytes = request.MaxOutputBytes;
		try {
			ArgumentNullException.ThrowIfNull(request.Profile);
			var initialized = await _connection.RequestAsync("initialize", InitializeParameters, ct).ConfigureAwait(false);
			var capabilities = AcpCapabilities.Read(initialized);
			if (request.Images.Count > 0
				&& !AcpCapabilities.Boolean(capabilities, "promptCapabilities", "image")) {
				return Failure(
					model,
					InferenceFailureKind.InputRejected,
					$"The ACP agent '{_connection.Definition.Name}' does not accept image prompts.");
			}

			var setup = await _connection.RequestAsync("session/new", new {
				cwd = Path.GetFullPath(request.Workspace),
				mcpServers = Array.Empty<object>(),
			}, ct).ConfigureAwait(false);

			string sessionId = RequiredString(setup, "sessionId");
			var configured = await AcpProfile.ApplyAsync(_connection, sessionId, setup, request.Profile, ct).ConfigureAwait(false);
			model = CurrentModel(configured) ?? _connection.Definition.Id;

			var turn = await _connection.RequestAsync("session/prompt", new {
				sessionId,
				prompt = BuildPrompt(request),
			}, ct).ConfigureAwait(false);

			string stopReason = RequiredString(turn, "stopReason");
			if (stopReason == "refusal") {
				return Failure(model, InferenceFailureKind.Refused, "The ACP agent refused the inference request.");
			}
			if (stopReason != "end_turn") {
				return Failure(model, InferenceFailureKind.InvalidResponse,
					$"The ACP agent ended the inference turn with stop reason '{stopReason}'.");
			}

			var usage = ReadUsage(turn);
			if (_replyOverflowed) {
				return Failure(model, InferenceFailureKind.InvalidResponse,
					"The ACP agent streamed more content than the query's output limit allows.", usage);
			}

			string reply;
			lock (_replyGate) reply = _reply.ToString();
			return Decode(reply.Trim(), model, usage);
		} catch (OperationCanceledException) {
			throw;
		} catch (AcpAuthenticationRequiredException) {
			return Failure(model, InferenceFailureKind.AuthenticationFailed,
				$"The ACP agent '{_connection.Definition.Name}' requires authentication. Open a session with it to sign in.");
		} catch (AcpInferenceProfileException ex) {
			return Failure(model, InferenceFailureKind.NotConfigured, ex.Message);
		} catch (AcpProtocolException ex) {
			return Failure(model, InferenceFailureKind.ProviderUnavailable, ex.Message);
		} catch (Exception ex) when (ex is IOException or InvalidOperationException) {
			return Failure(model, InferenceFailureKind.ProviderUnavailable,
				$"The ACP agent '{_connection.Definition.Name}' did not complete the inference query.");
		}
	}

	// ACP has no output-schema field, so the schema travels in the prompt and Weavie enforces it locally.
	private static object[] BuildPrompt(InferenceProviderRequest request) {
		var blocks = new List<object> { new {
			type = "text",
			text = request.Prompt
				+ "\n\nRespond with exactly one JSON value matching this schema, and nothing else — no prose, no "
				+ "explanation, and no markdown code fences. Do not use any tools.\n\nSchema:\n"
				+ request.OutputSchemaJson,
		} };
		blocks.AddRange(request.Images.Select(image => (object)new {
			type = "image",
			mimeType = image.Mime,
			data = Convert.ToBase64String(image.Bytes.Span),
		}));
		return [.. blocks];
	}

	private static InferenceProviderResult Decode(string reply, string model, InferenceUsage? usage) {
		if (reply.Length == 0) {
			return Failure(model, InferenceFailureKind.InvalidResponse, "The ACP agent returned no content.", usage);
		}
		try {
			using var document = JsonDocument.Parse(reply);
		} catch (JsonException) {
			return Failure(model, InferenceFailureKind.InvalidResponse,
				"The ACP agent returned text that is not exactly one JSON value.", usage);
		}

		return new InferenceProviderSuccess { ModelId = model, OutputJson = reply, Usage = usage };
	}

	private static InferenceUsage? ReadUsage(JsonElement turn) {
		if (!turn.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
		return new InferenceUsage {
			InputTokens = Number(usage, "inputTokens"),
			CachedInputTokens = Number(usage, "cachedReadTokens"),
			OutputTokens = Number(usage, "outputTokens"),
		};
	}

	private static long Number(JsonElement parent, string property) =>
		parent.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
			&& value.TryGetInt64(out long result)
				? result
				: 0;

	// The final configuration is authoritative: later control mutations may change the selected model.
	private static string? CurrentModel(IReadOnlyList<AgentControlAxis> controls) =>
		controls.FirstOrDefault(control => control.Category == "model")?.Value;

	private static InferenceProviderFailure Failure(
		string model,
		InferenceFailureKind kind,
		string detail) => Failure(model, kind, detail, usage: null);

	private static InferenceProviderFailure Failure(
		string model,
		InferenceFailureKind kind,
		string detail,
		InferenceUsage? usage) => new() {
			ModelId = model,
			Kind = kind,
			Detail = detail,
			Usage = usage,
		};

	internal static string RequiredString(JsonElement value, string property) =>
		value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
			&& result.GetString() is { Length: > 0 } text
				? text
				: throw new AcpProtocolException($"The ACP response is missing '{property}'.");

	private void Collect(string method, JsonElement parameters) {
		if (method != "session/update"
			|| parameters.ValueKind != JsonValueKind.Object
			|| !parameters.TryGetProperty("update", out var update)
			|| !update.TryGetProperty("sessionUpdate", out var kind)
			|| kind.ValueKind != JsonValueKind.String
			|| kind.GetString() != "agent_message_chunk"
			|| !update.TryGetProperty("content", out var content)
			|| !content.TryGetProperty("text", out var text)
			|| text.ValueKind != JsonValueKind.String) {
			return;
		}

		string? chunk = text.GetString();
		if (chunk is null) return;
		lock (_replyGate) {
			// Bound accumulation so a runaway agent cannot balloon the host before the service's size check.
			_replyBytes += Encoding.UTF8.GetByteCount(chunk);
			if (_replyBytes > _maxReplyBytes) {
				_replyOverflowed = true;
				return;
			}
			_reply.Append(chunk);
		}
	}
}
