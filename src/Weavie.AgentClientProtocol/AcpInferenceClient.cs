using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
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
internal sealed partial class AcpInferenceClient : IAsyncDisposable {
	private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
	private readonly AcpAgentDefinition _definition;
	private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
	private readonly StringBuilder _reply = new();
	private readonly Lock _replyGate = new();
	private readonly Process _process;
	private long _nextId;
	private int _replyBytes;
	private int _maxReplyBytes = int.MaxValue;
	private volatile bool _replyOverflowed;
	private volatile bool _disposed;

	private AcpInferenceClient(AcpAgentDefinition definition, Process process) {
		_definition = definition;
		_process = process;
	}

	/// <summary>Runs exactly one query. Never retries, restarts, or falls back to another agent.</summary>
	public static async Task<InferenceProviderResult> QueryAsync(
		AcpAgentDefinition definition,
		InferenceProviderRequest request,
		CancellationToken ct) {
		AcpInferenceClient client;
		try {
			client = Start(definition, request.Workspace);
		} catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or IOException
			or InvalidOperationException or UnauthorizedAccessException) {
			return Failure(definition.Id, InferenceFailureKind.NotConfigured,
				$"The ACP agent '{definition.Name}' could not be started.");
		}

		await using (client) {
			return await client.RunAsync(request, ct).ConfigureAwait(false);
		}
	}

	private static AcpInferenceClient Start(AcpAgentDefinition definition, string workspace) {
		string directory = Path.GetFullPath(workspace);
		var invocation = AcpProcessInvocation.ResolveRedirectedProcess(definition, directory, []);
		string command = invocation.Command;
		var arguments = invocation.Arguments;

		var info = new ProcessStartInfo(command) {
			WorkingDirectory = directory,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardInputEncoding = Utf8NoBom,
			StandardOutputEncoding = Utf8NoBom,
			StandardErrorEncoding = Utf8NoBom,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (string argument in arguments) info.ArgumentList.Add(argument);
		foreach (var entry in definition.Environment) info.Environment[entry.Key] = entry.Value;

		var process = new Process { StartInfo = info, EnableRaisingEvents = false };
		if (!process.Start()) {
			process.Dispose();
			throw new InvalidOperationException($"ACP agent '{definition.Name}' did not start.");
		}

		var client = new AcpInferenceClient(definition, process);
		_ = client.ReadStdoutAsync();
		_ = DrainStderrAsync(process);
		return client;
	}

	// Read stderr so a chatty agent cannot fill its pipe buffer and stall; inference never surfaces its content.
	private static async Task DrainStderrAsync(Process process) {
		try {
			await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) {
			// The process ended first.
		}
	}

	private async Task<InferenceProviderResult> RunAsync(InferenceProviderRequest request, CancellationToken ct) {
		string model = _definition.Id;
		_maxReplyBytes = request.MaxOutputBytes;
		try {
			ArgumentNullException.ThrowIfNull(request.Profile);
			var initialized = await RequestAsync("initialize", new {
				protocolVersion = 1,
				clientCapabilities = new {
					session = new { configOptions = new { boolean = new { } } },
				},
				clientInfo = new { name = "weavie", title = "Weavie", version = "1" },
			}, ct).ConfigureAwait(false);
			var capabilities = AcpCapabilities.Read(initialized);
			if (request.Images.Count > 0
				&& !AcpCapabilities.Boolean(capabilities, "promptCapabilities", "image")) {
				return Failure(
					model,
					InferenceFailureKind.InputRejected,
					$"The ACP agent '{_definition.Name}' does not accept image prompts.");
			}

			var setup = await RequestAsync("session/new", new {
				cwd = Path.GetFullPath(request.Workspace),
				mcpServers = Array.Empty<object>(),
			}, ct).ConfigureAwait(false);

			string sessionId = RequiredString(setup, "sessionId");
			var configured = await ApplyProfileAsync(sessionId, setup, request.Profile, ct).ConfigureAwait(false);
			model = CurrentModel(configured) ?? _definition.Id;

			var turn = await RequestAsync("session/prompt", new {
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
				$"The ACP agent '{_definition.Name}' requires authentication. Open a session with it to sign in.");
		} catch (AcpInferenceProfileException ex) {
			return Failure(model, InferenceFailureKind.NotConfigured, ex.Message);
		} catch (AcpProtocolException ex) {
			return Failure(model, InferenceFailureKind.ProviderUnavailable, ex.Message);
		} catch (Exception ex) when (ex is IOException or InvalidOperationException) {
			return Failure(model, InferenceFailureKind.ProviderUnavailable,
				$"The ACP agent '{_definition.Name}' did not complete the inference query.");
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

	private static string RequiredString(JsonElement value, string property) =>
		value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String
			&& result.GetString() is { Length: > 0 } text
				? text
				: throw new AcpProtocolException($"The ACP inference response is missing '{property}'.");

	private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct) {
		ct.ThrowIfCancellationRequested();
		long id = Interlocked.Increment(ref _nextId);
		var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = completion;
		try {
			await WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters }).ConfigureAwait(false);
		} catch {
			_pending.TryRemove(id, out _);
			throw;
		}

		using var registration = ct.Register(() => {
			if (_pending.TryRemove(id, out var pending)) pending.TrySetCanceled(ct);
		});
		return await completion.Task.ConfigureAwait(false);
	}

	private async Task WriteAsync(object payload) {
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
		await _process.StandardInput.FlushAsync().ConfigureAwait(false);
	}

	private async Task ReadStdoutAsync() {
		try {
			while (await _process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line) {
				if (line.Length == 0) continue;
				try {
					Handle(line);
				} catch (JsonException) {
					FailPending(new AcpProtocolException("The ACP agent wrote a malformed inference message."));
					return;
				}
			}
		} catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) {
			// Falls through to the shared teardown below.
		}

		FailPending(new IOException("The ACP agent closed its output before answering the inference query."));
	}

	private void Handle(string line) {
		using var document = JsonDocument.Parse(line);
		var root = document.RootElement;
		bool hasId = root.TryGetProperty("id", out var id);
		string? method = root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String
			? m.GetString()
			: null;

		if (hasId && method is null) {
			if (!_pending.TryRemove(ReadId(id), out var pending)) return;
			if (root.TryGetProperty("error", out var error)) {
				pending.TrySetException(ReadError(error));
			} else if (root.TryGetProperty("result", out var result)) {
				pending.TrySetResult(result.Clone());
			} else {
				pending.TrySetException(new AcpProtocolException("An ACP response carried no result or error."));
			}
			return;
		}

		if (hasId && method is not null) {
			// Every agent request is refused: inference gets no tools, filesystem, terminal, or elicitation.
			_ = RefuseAsync(ReadId(id), method);
			return;
		}

		if (method == "session/update") Collect(root);
	}

	private async Task RefuseAsync(long id, string method) {
		try {
			await WriteAsync(new {
				jsonrpc = "2.0",
				id,
				error = new { code = -32601, message = $"Weavie inference does not serve '{method}'." },
			}).ConfigureAwait(false);
		} catch (Exception ex) when (ex is IOException or InvalidOperationException) {
			// The agent is already gone; the pending query fails on its own.
		}
	}

	private void Collect(JsonElement notification) {
		if (!notification.TryGetProperty("params", out var parameters)
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

	private static long ReadId(JsonElement id) =>
		id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out long value) ? value : -1;

	private static Exception ReadError(JsonElement error) {
		int code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
			&& c.TryGetInt32(out int parsed)
				? parsed
				: 0;
		string message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
			? m.GetString() ?? "unknown"
			: "unknown";
		return code == -32000
			? new AcpAuthenticationRequiredException(message)
			: new AcpProtocolException($"The ACP agent rejected an inference request: {message}");
	}

	private void FailPending(Exception fault) {
		foreach (long key in _pending.Keys) {
			if (_pending.TryRemove(key, out var pending)) pending.TrySetException(fault);
		}
	}

	public async ValueTask DisposeAsync() {
		if (_disposed) return;
		_disposed = true;
		FailPending(new ObjectDisposedException(nameof(AcpInferenceClient)));
		try {
			if (!_process.HasExited) _process.Kill(entireProcessTree: true);
		} catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception) {
			// The process already exited; nothing to terminate.
		}

		try {
			await _process.WaitForExitAsync().ConfigureAwait(false);
		} catch (InvalidOperationException) {
			// The handle is already gone.
		} finally {
			_process.Dispose();
		}
	}
}

/// <summary>The ACP agent demanded authentication that ad-hoc inference cannot perform.</summary>
internal sealed class AcpAuthenticationRequiredException : Exception {
	public AcpAuthenticationRequiredException(string message) : base(message) { }
}
