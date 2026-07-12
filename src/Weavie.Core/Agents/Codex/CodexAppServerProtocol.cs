using System.Text.Json;
using Weavie.Core.Json;

namespace Weavie.Core.Agents.Codex;

/// <summary>Builds and interprets Codex app-server JSON-RPC messages over JSONL stdio.</summary>
public static class CodexAppServerProtocol {
	/// <summary>Builds the initial JSON-RPC initialize request.</summary>
	public static string Initialize(long id, string version) {
		ArgumentException.ThrowIfNullOrEmpty(version);
		return JsonSerializer.Serialize(new {
			method = "initialize",
			id,
			@params = new {
				clientInfo = new {
					name = "weavie",
					title = "Weavie",
					version,
				},
				capabilities = new {
					experimentalApi = true,
					optOutNotificationMethods = new[] {
						"hook/started",
						"hook/completed",
						"item/agentMessage/delta",
						"item/plan/delta",
						"item/commandExecution/outputDelta",
						"item/fileChange/outputDelta",
						"remoteControl/status/changed",
					},
				},
			},
		});
	}

	/// <summary>Builds the initialized notification that completes the app-server handshake.</summary>
	public static string Initialized() =>
		"{\"method\":\"initialized\",\"params\":{}}";

	/// <summary>Builds a thread/start request, omitting an empty model so Codex uses its configured default.</summary>
	public static string ThreadStart(
		long id,
		string model,
		string cwd,
		string sandbox,
		string approvalPolicy,
		string developerInstructions) {
		ArgumentNullException.ThrowIfNull(model);
		ArgumentException.ThrowIfNullOrEmpty(cwd);
		ArgumentException.ThrowIfNullOrEmpty(sandbox);
		ArgumentException.ThrowIfNullOrEmpty(approvalPolicy);
		ArgumentException.ThrowIfNullOrEmpty(developerInstructions);
		object parameters = string.IsNullOrWhiteSpace(model)
			? new { cwd, sandbox, approvalPolicy, developerInstructions }
			: new { cwd, sandbox, approvalPolicy, developerInstructions, model };
		return JsonSerializer.Serialize(new { method = "thread/start", id, @params = parameters });
	}

	/// <summary>Builds a thread/resume request for a persisted Codex thread id, preserving Weavie's session policy.</summary>
	public static string ThreadResume(
		long id,
		string threadId,
		string model,
		string cwd,
		string sandbox,
		string approvalPolicy,
		string developerInstructions) {
		ArgumentException.ThrowIfNullOrEmpty(threadId);
		ArgumentNullException.ThrowIfNull(model);
		ArgumentException.ThrowIfNullOrEmpty(cwd);
		ArgumentException.ThrowIfNullOrEmpty(sandbox);
		ArgumentException.ThrowIfNullOrEmpty(approvalPolicy);
		ArgumentException.ThrowIfNullOrEmpty(developerInstructions);
		object parameters = string.IsNullOrWhiteSpace(model)
			? new { threadId, cwd, sandbox, approvalPolicy, developerInstructions }
			: new { threadId, cwd, sandbox, approvalPolicy, developerInstructions, model };
		return JsonSerializer.Serialize(new { method = "thread/resume", id, @params = parameters });
	}

	/// <summary>Builds a turn/start request rooted in <paramref name="cwd"/> with one text input item.</summary>
	public static string TurnStart(
		long id,
		string threadId,
		string prompt,
		string cwd,
		string sandbox,
		string approvalPolicy) {
		ArgumentException.ThrowIfNullOrEmpty(threadId);
		ArgumentException.ThrowIfNullOrEmpty(prompt);
		ArgumentException.ThrowIfNullOrEmpty(cwd);
		ArgumentException.ThrowIfNullOrEmpty(sandbox);
		ArgumentException.ThrowIfNullOrEmpty(approvalPolicy);
		return TurnStartWithInput(id, threadId, cwd, sandbox, approvalPolicy, [TextInput(prompt)]);
	}

	/// <summary>Builds a turn/start request with text plus attached local image input items.</summary>
	public static string TurnStartWithImages(
		long id,
		string threadId,
		string prompt,
		IReadOnlyList<string> imagePaths,
		string cwd,
		string sandbox,
		string approvalPolicy) {
		ArgumentException.ThrowIfNullOrEmpty(threadId);
		ArgumentNullException.ThrowIfNull(prompt);
		ArgumentNullException.ThrowIfNull(imagePaths);
		ArgumentException.ThrowIfNullOrEmpty(cwd);
		ArgumentException.ThrowIfNullOrEmpty(sandbox);
		ArgumentException.ThrowIfNullOrEmpty(approvalPolicy);
		return TurnStartWithInput(id, threadId, cwd, sandbox, approvalPolicy, InputItems(prompt, imagePaths));
	}

	/// <summary>Builds a turn/steer request for an in-flight turn.</summary>
	public static string TurnSteer(long id, string threadId, string turnId, string prompt) {
		ArgumentException.ThrowIfNullOrEmpty(threadId);
		ArgumentException.ThrowIfNullOrEmpty(turnId);
		ArgumentException.ThrowIfNullOrEmpty(prompt);
		return TurnSteerWithInput(id, threadId, turnId, [TextInput(prompt)]);
	}

	/// <summary>Builds a turn/steer request with text plus attached local image input items.</summary>
	public static string TurnSteerWithImages(long id, string threadId, string turnId, string prompt, IReadOnlyList<string> imagePaths) {
		ArgumentException.ThrowIfNullOrEmpty(threadId);
		ArgumentException.ThrowIfNullOrEmpty(turnId);
		ArgumentNullException.ThrowIfNull(prompt);
		ArgumentNullException.ThrowIfNull(imagePaths);
		return TurnSteerWithInput(id, threadId, turnId, InputItems(prompt, imagePaths));
	}

	private static string TurnStartWithInput(
		long id,
		string threadId,
		string cwd,
		string sandbox,
		string approvalPolicy,
		object[] input) =>
		JsonSerializer.Serialize(new {
			method = "turn/start",
			id,
			@params = new {
				threadId,
				cwd,
				approvalPolicy,
				sandboxPolicy = SandboxPolicy(sandbox, cwd),
				input,
			},
		});

	private static string TurnSteerWithInput(long id, string threadId, string turnId, object[] input) =>
		JsonSerializer.Serialize(new {
			method = "turn/steer",
			id,
			@params = new {
				threadId,
				expectedTurnId = turnId,
				input,
			},
		});

	private static object TextInput(string text) => new { type = "text", text };

	private static object LocalImageInput(string path) => new { type = "localImage", path };

	private static object[] InputItems(string prompt, IReadOnlyList<string> imagePaths) {
		List<object> input = [];
		if (prompt.Length > 0) {
			input.Add(TextInput(prompt));
		}

		foreach (string path in imagePaths) {
			ArgumentException.ThrowIfNullOrEmpty(path);
			input.Add(LocalImageInput(path));
		}

		if (input.Count == 0) {
			throw new ArgumentException("Codex input must include text or at least one image.", nameof(imagePaths));
		}

		return [.. input];
	}

	/// <summary>Builds a turn/interrupt request for an active turn.</summary>
	public static string TurnInterrupt(long id, string threadId, string turnId) {
		ArgumentException.ThrowIfNullOrEmpty(threadId);
		ArgumentException.ThrowIfNullOrEmpty(turnId);
		return JsonSerializer.Serialize(new { method = "turn/interrupt", id, @params = new { threadId, turnId } });
	}

	/// <summary>Extracts a thread id from a response to <c>thread/start</c> or <c>thread/resume</c>.</summary>
	public static bool TryReadThreadId(string line, out string threadId) {
		ArgumentNullException.ThrowIfNull(line);
		threadId = string.Empty;
		using var doc = JsonDocument.Parse(line);
		if (!doc.RootElement.TryGetProperty("result", out var result)
			|| !result.TryGetProperty("thread", out var thread)
			|| !thread.TryGetProperty("id", out var id)
			|| id.ValueKind != JsonValueKind.String) {
			return false;
		}

		threadId = id.GetString() ?? string.Empty;
		return threadId.Length > 0;
	}

	/// <summary>Maps documented app-server notifications into Weavie's normalized agent events.</summary>
	public static bool TryAdaptNotification(string line, out AgentEvent value) {
		ArgumentNullException.ThrowIfNull(line);
		value = new AgentOtherEvent();
		using var doc = JsonDocument.Parse(line);
		if (!doc.RootElement.TryGetProperty("method", out var methodElement)
			|| methodElement.ValueKind != JsonValueKind.String) {
			return false;
		}

		string method = methodElement.GetString() ?? string.Empty;
		value = method switch {
			"thread/started" => new AgentSessionStarted("startup"),
			"turn/started" => new AgentPromptSubmitted(null),
			"turn/completed" => new AgentTurnStopped(false),
			"turn/interrupted" => new AgentTurnStopped(false),
			"item/started" when TryReadMutation(doc.RootElement, out var mutation) => new AgentToolStarting(mutation),
			"item/completed" when TryReadMutation(doc.RootElement, out var mutation) => new AgentToolCompleted(mutation),
			_ => new AgentOtherEvent(),
		};
		return true;
	}

	private static bool TryReadMutation(JsonElement root, out AgentMutation mutation) {
		mutation = new AgentMutation.None();
		if (!root.TryGetProperty("params", out var parameters)
			|| !parameters.TryGetProperty("item", out var item)) {
			return false;
		}

		string type = item.GetStringOrEmpty("type");
		if (string.Equals(type, "fileChange", StringComparison.Ordinal)) {
			mutation = ReadFileChangeMutation(item);
			return true;
		}

		if (string.Equals(type, "commandExecution", StringComparison.Ordinal)
			|| string.Equals(type, "mcpToolCall", StringComparison.Ordinal)
			|| string.Equals(type, "dynamicToolCall", StringComparison.Ordinal)) {
			string itemId = item.GetStringOrEmpty("id");
			if (itemId.Length == 0) {
				return false;
			}

			mutation = new AgentMutation.Workspace(itemId);
			return true;
		}

		return false;
	}

	private static AgentMutation ReadFileChangeMutation(JsonElement item) {
		if (!item.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array) {
			return new AgentMutation.None();
		}

		List<AgentMutation.File> files = [];
		foreach (var change in changes.EnumerateArray()) {
			string path = change.GetStringOrEmpty("path");
			if (path.Length > 0) {
				files.Add(new AgentMutation.File(path, Cwd: null, ProvidesEditLocation: true));
			}
		}

		return files.Count switch {
			0 => new AgentMutation.None(),
			1 => files[0],
			_ => new AgentMutation.Files(files),
		};
	}

	private static object SandboxPolicy(string sandbox, string cwd) =>
		sandbox switch {
			"read-only" => new { type = "readOnly", networkAccess = false },
			"workspace-write" => new { type = "workspaceWrite", networkAccess = false, writableRoots = new[] { cwd } },
			"danger-full-access" => new { type = "dangerFullAccess" },
			_ => throw new ArgumentOutOfRangeException(nameof(sandbox), sandbox, "Unsupported Codex sandbox mode."),
		};
}
