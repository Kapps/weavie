using System.Text.Json;
using Weavie.Core.Json;

namespace Weavie.Core.Agents.Codex;

/// <summary>A discoverable Codex skill: the name and path a structured skill turn input needs, plus its description.</summary>
public sealed record CodexSkill(string Name, string Path, string Description);

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

	/// <summary>Builds a hooks/list request for the session working directory.</summary>
	public static string HooksList(long id, string cwd) {
		ArgumentException.ThrowIfNullOrEmpty(cwd);
		return JsonSerializer.Serialize(new { method = "hooks/list", id, @params = new { cwds = new[] { cwd } } });
	}

	/// <summary>Builds a model/list request for the models offered to the user in the status line picker.</summary>
	public static string ModelList(long id, bool includeHidden) =>
		JsonSerializer.Serialize(new { method = "model/list", id, @params = new { includeHidden } });

	/// <summary>Builds a skills/list request for the skills discoverable from the session working directory.</summary>
	public static string SkillsList(long id, string cwd) {
		ArgumentException.ThrowIfNullOrEmpty(cwd);
		return JsonSerializer.Serialize(new { method = "skills/list", id, @params = new { cwds = new[] { cwd } } });
	}

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
		string approvalPolicy,
		string model) =>
		TurnStart(id, threadId, prompt, cwd, sandbox, approvalPolicy, model, string.Empty, string.Empty);

	/// <summary>Builds a turn/start request with model, reasoning-effort, and service-tier overrides.</summary>
	public static string TurnStart(
		long id,
		string threadId,
		string prompt,
		string cwd,
		string sandbox,
		string approvalPolicy,
		string model,
		string reasoningEffort,
		string serviceTier) {
		ArgumentException.ThrowIfNullOrEmpty(threadId);
		ArgumentException.ThrowIfNullOrEmpty(prompt);
		ArgumentException.ThrowIfNullOrEmpty(cwd);
		ArgumentException.ThrowIfNullOrEmpty(sandbox);
		ArgumentException.ThrowIfNullOrEmpty(approvalPolicy);
		ArgumentNullException.ThrowIfNull(model);
		return TurnStartWithInput(id, threadId, cwd, sandbox, approvalPolicy, model, reasoningEffort, serviceTier, [TextInput(prompt)]);
	}

	/// <summary>Builds a turn/start request with text plus attached local images and staged skill input items.</summary>
	public static string TurnStartWithInputs(
		long id,
		string threadId,
		string prompt,
		IReadOnlyList<string> imagePaths,
		IReadOnlyList<CodexSkill> skills,
		string cwd,
		string sandbox,
		string approvalPolicy,
		string model) =>
		TurnStartWithInputs(id, threadId, prompt, imagePaths, skills, cwd, sandbox, approvalPolicy, model, string.Empty, string.Empty);

	/// <summary>Builds a turn/start request with structured inputs and model execution overrides.</summary>
	public static string TurnStartWithInputs(
		long id,
		string threadId,
		string prompt,
		IReadOnlyList<string> imagePaths,
		IReadOnlyList<CodexSkill> skills,
		string cwd,
		string sandbox,
		string approvalPolicy,
		string model,
		string reasoningEffort,
		string serviceTier) {
		ArgumentException.ThrowIfNullOrEmpty(threadId);
		ArgumentNullException.ThrowIfNull(prompt);
		ArgumentNullException.ThrowIfNull(imagePaths);
		ArgumentNullException.ThrowIfNull(skills);
		ArgumentException.ThrowIfNullOrEmpty(cwd);
		ArgumentException.ThrowIfNullOrEmpty(sandbox);
		ArgumentException.ThrowIfNullOrEmpty(approvalPolicy);
		ArgumentNullException.ThrowIfNull(model);
		return TurnStartWithInput(id, threadId, cwd, sandbox, approvalPolicy, model, reasoningEffort, serviceTier, InputItems(prompt, imagePaths, skills));
	}

	/// <summary>Builds a turn/steer request for an in-flight turn.</summary>
	public static string TurnSteer(long id, string threadId, string turnId, string prompt) {
		ArgumentException.ThrowIfNullOrEmpty(threadId);
		ArgumentException.ThrowIfNullOrEmpty(turnId);
		ArgumentException.ThrowIfNullOrEmpty(prompt);
		return TurnSteerWithInput(id, threadId, turnId, [TextInput(prompt)]);
	}

	/// <summary>Builds a turn/steer request with text plus attached local images and staged skill input items.</summary>
	public static string TurnSteerWithInputs(
		long id,
		string threadId,
		string turnId,
		string prompt,
		IReadOnlyList<string> imagePaths,
		IReadOnlyList<CodexSkill> skills) {
		ArgumentException.ThrowIfNullOrEmpty(threadId);
		ArgumentException.ThrowIfNullOrEmpty(turnId);
		ArgumentNullException.ThrowIfNull(prompt);
		ArgumentNullException.ThrowIfNull(imagePaths);
		ArgumentNullException.ThrowIfNull(skills);
		return TurnSteerWithInput(id, threadId, turnId, InputItems(prompt, imagePaths, skills));
	}

	private static string TurnStartWithInput(
		long id,
		string threadId,
		string cwd,
		string sandbox,
		string approvalPolicy,
		string model,
		string reasoningEffort,
		string serviceTier,
		object[] input) {
		// An empty model leaves the thread's current model untouched; a value overrides it for this and
		// subsequent turns, which is how a live in-session model change takes effect without a restart.
		Dictionary<string, object> parameters = new() {
			["threadId"] = threadId,
			["cwd"] = cwd,
			["approvalPolicy"] = approvalPolicy,
			["sandboxPolicy"] = SandboxPolicy(sandbox, cwd),
			["input"] = input,
		};
		if (!string.IsNullOrWhiteSpace(model)) parameters["model"] = model;
		if (!string.IsNullOrWhiteSpace(reasoningEffort)) parameters["effort"] = reasoningEffort;
		if (!string.IsNullOrWhiteSpace(serviceTier)) parameters["serviceTier"] = serviceTier;
		return JsonSerializer.Serialize(new { method = "turn/start", id, @params = parameters });
	}

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

	private static object SkillInput(CodexSkill skill) => new { type = "skill", name = skill.Name, path = skill.Path };

	private static object[] InputItems(string prompt, IReadOnlyList<string> imagePaths, IReadOnlyList<CodexSkill> skills) {
		List<object> input = [];
		if (prompt.Length > 0) {
			input.Add(TextInput(prompt));
		}

		foreach (string path in imagePaths) {
			ArgumentException.ThrowIfNullOrEmpty(path);
			input.Add(LocalImageInput(path));
		}

		foreach (var skill in skills) {
			input.Add(SkillInput(skill));
		}

		if (input.Count == 0) {
			throw new ArgumentException("Codex input must include text, an image, or a skill.", nameof(prompt));
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

	/// <summary>Reads a model/list result into selectable model options and the id of the catalog default.</summary>
	public static bool TryReadModels(JsonElement result, out IReadOnlyList<AgentControlOption> models, out string defaultModel) {
		models = [];
		defaultModel = string.Empty;
		if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) {
			return false;
		}

		List<AgentControlOption> options = [];
		foreach (var item in data.EnumerateArray()) {
			string modelId = item.GetStringOrEmpty("id");
			if (modelId.Length == 0) {
				continue;
			}

			string label = item.GetStringOrEmpty("displayName");
			string description = item.GetStringOrEmpty("description");
			options.Add(new AgentControlOption {
				Id = modelId,
				Label = label.Length > 0 ? label : modelId,
				Description = description.Length > 0 ? description : null,
			});
			if (item.TryGetProperty("isDefault", out var isDefault) && isDefault.ValueKind == JsonValueKind.True) {
				defaultModel = modelId;
			}
		}

		models = options;
		return true;
	}

	/// <summary>Reads a skills/list result into the session's skills, skipping disabled ones.</summary>
	public static bool TryReadSkills(JsonElement result, out IReadOnlyList<CodexSkill> skills) {
		skills = [];
		if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) {
			return false;
		}

		List<CodexSkill> parsed = [];
		foreach (var group in data.EnumerateArray()) {
			if (!group.TryGetProperty("skills", out var groupSkills) || groupSkills.ValueKind != JsonValueKind.Array) {
				continue;
			}

			foreach (var skill in groupSkills.EnumerateArray()) {
				var value = ReadSkill(skill);
				if (value is not null) {
					parsed.Add(value);
				}
			}
		}

		skills = parsed;
		return true;
	}

	private static CodexSkill? ReadSkill(JsonElement skill) {
		string name = skill.GetStringOrEmpty("name");
		string path = skill.GetStringOrEmpty("path");
		if (name.Length == 0 || path.Length == 0
			|| (skill.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)) {
			return null;
		}

		skill.TryGetProperty("interface", out var face);
		string shortDescription = face.ValueKind == JsonValueKind.Object ? face.GetStringOrEmpty("shortDescription") : string.Empty;
		string description = shortDescription.Length > 0 ? shortDescription : skill.GetStringOrEmpty("description");
		return new CodexSkill(name, path, description);
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
			// Codex's turn-start carries no prompt text; a correction it drains records with a null prompt.
			"turn/started" => new AgentPromptSubmitted(null, null),
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
