using System.ComponentModel;
using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;

namespace Weavie.Hosting.Inference.Claude;

internal sealed class ClaudeCliInference : IInferenceProvider {
	private const int EnvelopeOverheadBytes = 64 * 1024;
	private readonly SettingsStore _settings;
	private readonly IAgentCliProcessRunner _processes;

	public ClaudeCliInference(SettingsStore settings, IAgentCliProcessRunner processes) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(processes);
		_settings = settings;
		_processes = processes;
	}

	public InferenceProviderInfo InferenceInfo { get; } = new() {
		Categories = [InferenceModelCategory.Utility, InferenceModelCategory.Reasoning],
	};

	public async Task<InferenceProviderResult> QueryInferenceAsync(
		InferenceProviderRequest request,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		var profile = Profile(request.Category);
		try {
			string command = _settings.RequireString(CoreSettings.ClaudePath);
			if (string.IsNullOrWhiteSpace(command)) {
				return NotConfigured(profile.Model);
			}
			var result = await _processes.RunAsync(new AgentCliProcessRequest {
				Command = command,
				WorkingDirectory = Path.GetFullPath(request.Workspace),
				Arguments = [
					"--print",
					"--safe-mode",
					"--tools", "",
					"--strict-mcp-config",
					"--disable-slash-commands",
					"--no-session-persistence",
					"--output-format", "json",
					"--json-schema", request.OutputSchemaJson,
					"--model", profile.Model,
					"--effort", profile.Effort,
				],
				PathEntries = [],
				Environment = new Dictionary<string, string>(StringComparer.Ordinal),
				RemoveEnvironment = [],
				StandardInput = request.Prompt,
				MaxCapturedStdoutBytes = request.MaxOutputBytes + EnvelopeOverheadBytes,
				CaptureStdout = true,
			}, ct).ConfigureAwait(false);
			if (result.ExitCode != 0) {
				return Failure(profile.Model, $"Claude inference exited with code {result.ExitCode}.");
			}

			return Parse(result.StandardOutput, profile.Model);
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException) {
			return NotConfigured(profile.Model);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return Failure(profile.Model, "The Claude inference process failed.");
		}
	}

	private static InferenceProviderResult Parse(string json, string model) {
		try {
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True) {
				return Failure(model, "Claude reported an inference failure.");
			}
			if (!root.TryGetProperty("structured_output", out var output)) {
				return Invalid(model, "Claude returned no structured output.");
			}

			return new InferenceProviderSuccess {
				ModelId = model,
				RequestId = String(root, "session_id"),
				OutputJson = output.GetRawText(),
			};
		} catch (JsonException) {
			return Invalid(model, "Claude returned a malformed result envelope.");
		}
	}

	private static InferenceProviderFailure Failure(string model, string detail) => new() {
		ModelId = model,
		Kind = InferenceFailureKind.ProviderUnavailable,
		Detail = detail,
	};

	private static InferenceProviderFailure NotConfigured(string model) => new() {
		ModelId = model,
		Kind = InferenceFailureKind.NotConfigured,
		Detail = "The configured Claude CLI could not be started.",
	};

	private static InferenceProviderFailure Invalid(string model, string detail) => new() {
		ModelId = model,
		Kind = InferenceFailureKind.InvalidResponse,
		Detail = detail,
	};

	private static string? String(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;

	private static ClaudeProfile Profile(InferenceModelCategory category) => category switch {
		InferenceModelCategory.Utility => new ClaudeProfile("haiku", "low"),
		InferenceModelCategory.Reasoning => new ClaudeProfile("sonnet", "medium"),
		_ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown inference model category."),
	};

	private sealed record ClaudeProfile(string Model, string Effort);
}
