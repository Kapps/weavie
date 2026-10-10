using System.Globalization;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Inference;

namespace Weavie.Hosting.Inference.Claude;

/// <summary>Reads Claude's own model catalog from the CLI's <c>initialize</c> handshake; no model turn runs.</summary>
internal static class ClaudeCliControlProbe {
	private const int MaxOutputBytes = 4 * 1024 * 1024;
	private const string Initialize = """{"type":"control_request","request_id":"weavie-probe","request":{"subtype":"initialize"}}""";

	public static async Task<InferenceControls> ProbeAsync(
		IAgentCliProcessRunner processes,
		string command,
		string defaultModel,
		string defaultEffort,
		string model,
		CancellationToken ct) {
		if (string.IsNullOrWhiteSpace(command)) {
			throw new InvalidOperationException("The Claude CLI path isn't configured.");
		}

		var result = await processes.RunAsync(new AgentCliProcessRequest {
			Command = command,
			WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			Arguments = [
				.. ClaudeCliInference.IsolationArguments,
				"--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
			],
			PathEntries = [],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal),
			RemoveEnvironment = [],
			StandardInput = Initialize + "\n",
			MaxCapturedStdoutBytes = MaxOutputBytes,
			CaptureStdout = true,
		}, ct).ConfigureAwait(false);
		if (result.ExitCode != 0) {
			throw new InvalidOperationException($"Claude exited with code {result.ExitCode} before listing its models.");
		}

		return Parse(result.StandardOutput, defaultModel, defaultEffort, model);
	}

	internal static InferenceControls Parse(string output, string defaultModel, string defaultEffort, string model) {
		foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
			using var document = JsonDocument.Parse(line);
			var root = document.RootElement;
			if (root.TryGetProperty("type", out var type) && type.GetString() == "control_response"
				&& root.GetProperty("response").TryGetProperty("response", out var response)
				&& response.TryGetProperty("models", out var models)) {
				var catalog = models.EnumerateArray()
					.Where(entry => entry.GetProperty("value").GetString() != "default")
					.Select(entry => entry.Clone())
					.ToArray();
				bool fast = response.TryGetProperty("fast_mode_state", out var state) && state.GetString() == "on";
				return new InferenceControls {
					Defaults = Controls(catalog, defaultModel, defaultEffort, fast),
					Selected = Controls(catalog, model.Length > 0 ? model : defaultModel, defaultEffort, fast),
				};
			}
		}

		throw new InvalidOperationException("Claude didn't list its models.");
	}

	// The axes a query sees with `model` selected: Claude keeps Weavie's default effort until one is configured.
	private static AgentControlAxis[] Controls(JsonElement[] catalog, string model, string effort, bool fast) {
		var options = catalog.Select(entry => new AgentControlOption {
			Id = entry.GetProperty("value").GetString()!,
			Label = entry.GetProperty("displayName").GetString()!,
			Description = entry.TryGetProperty("description", out var description) ? description.GetString() : null,
		}).ToArray();
		var selected = catalog.FirstOrDefault(entry => entry.GetProperty("value").GetString() == model);
		var axes = new List<AgentControlAxis> {
			Select("model", "model", model, options),
		};
		if (selected.ValueKind == JsonValueKind.Object
			&& selected.TryGetProperty("supportedEffortLevels", out var levels)) {
			axes.Add(Select("effort", "thought_level", effort, [
				.. levels.EnumerateArray().Select(level => level.GetString()!).Select(level => new AgentControlOption {
					Id = level,
					Label = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(level),
				}),
			]));
		}
		if (selected.ValueKind == JsonValueKind.Object
			&& selected.TryGetProperty("supportsFastMode", out var supportsFast) && supportsFast.GetBoolean()) {
			axes.Add(new AgentControlAxis {
				Id = "fast",
				Label = "Fast mode",
				Kind = "boolean",
				Value = fast ? "true" : "false",
				ValueLabel = fast ? "On" : "Off",
				Options = [new() { Id = "true", Label = "On" }, new() { Id = "false", Label = "Off" }],
			});
		}
		return [.. axes];
	}

	private static AgentControlAxis Select(string id, string category, string value, AgentControlOption[] options) => new() {
		Id = id,
		Label = id,
		Category = category,
		Kind = "select",
		Value = value,
		ValueLabel = options.FirstOrDefault(option => option.Id == value)?.Label ?? value,
		Options = options,
	};
}
