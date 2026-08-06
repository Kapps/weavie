using System.ComponentModel;
using System.Text;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Hosting.Agents.Codex;

namespace Weavie.Hosting.Inference.Codex;

internal sealed class CodexCliInference : IInferenceProvider {
	private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
	private readonly SettingsStore _settings;
	private readonly IAgentCliProcessRunner _processes;

	public CodexCliInference(SettingsStore settings, IAgentCliProcessRunner processes) {
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
		string model = Model(request.Category);
		using var temp = InferenceTempDirectory.Create();
		string schemaPath = Path.Combine(temp.Path, "output-schema.json");
		string outputPath = Path.Combine(temp.Path, "output.json");
		try {
			string? command = _settings.GetString("codex.path");
			if (string.IsNullOrWhiteSpace(command)) {
				return NotConfigured(model);
			}

			await File.WriteAllTextAsync(schemaPath, request.OutputSchemaJson, Utf8, ct).ConfigureAwait(false);
			var launch = CodexInstallResolver.Resolve(command, temp.Path) with { WorkingDirectory = temp.Path };
			var result = await _processes.RunAsync(new AgentCliProcessRequest {
				Command = launch.Command,
				WorkingDirectory = launch.WorkingDirectory,
				Arguments = [
					"--ask-for-approval", "never",
					"exec",
					"--ephemeral",
					"--ignore-user-config",
					"--ignore-rules",
					"--strict-config",
					"--disable", "apps",
					"--disable", "browser_use",
					"--disable", "computer_use",
					"--disable", "hooks",
					"--disable", "image_generation",
					"--disable", "multi_agent",
					"--disable", "plugins",
					"--disable", "remote_plugin",
					"--disable", "shell_snapshot",
					"--disable", "shell_tool",
					"--disable", "workspace_dependencies",
					"--skip-git-repo-check",
					"--model", model,
					"-c", "default_permissions=\"weavie-inference\"",
					"-c", "permissions.weavie-inference.description=\"No local file or network access.\"",
					"-c", "permissions.weavie-inference.filesystem.:root=\"deny\"",
					"-c", "permissions.weavie-inference.network.enabled=false",
					"-c", "model_reasoning_effort=\"" + Effort(request.Category) + "\"",
					"-c", "tools.web_search=false",
					"-c", "web_search=\"disabled\"",
					"--output-schema", schemaPath,
					"--output-last-message", outputPath,
					"--color", "never",
					"-",
				],
				PathEntries = launch.PathEntries,
				Environment = new Dictionary<string, string>(StringComparer.Ordinal),
				RemoveEnvironment = [],
				StandardInput = request.Prompt,
				MaxCapturedStdoutBytes = 1,
				CaptureStdout = false,
			}, ct).ConfigureAwait(false);
			if (result.ExitCode != 0) {
				return Failure(model, $"Codex inference exited with code {result.ExitCode}.");
			}
			if (!File.Exists(outputPath)) {
				return Invalid(model, "Codex returned no structured output.");
			}
			if (new FileInfo(outputPath).Length > request.MaxOutputBytes) {
				return Invalid(model, "Codex returned structured output larger than the query permits.");
			}

			return new InferenceProviderSuccess {
				ModelId = model,
				OutputJson = await File.ReadAllTextAsync(outputPath, ct).ConfigureAwait(false),
			};
		} catch (OperationCanceledException) {
			throw;
		} catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or InvalidOperationException) {
			return NotConfigured(model);
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			return Failure(model, "The Codex inference process failed.");
		}
	}

	private static string Model(InferenceModelCategory category) => category switch {
		InferenceModelCategory.Utility => "gpt-5.6-luna",
		InferenceModelCategory.Reasoning => "gpt-5.6-sol",
		_ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown inference model category."),
	};

	private static string Effort(InferenceModelCategory category) => category switch {
		InferenceModelCategory.Utility => "low",
		InferenceModelCategory.Reasoning => "medium",
		_ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown inference model category."),
	};

	private static InferenceProviderFailure NotConfigured(string model) => new() {
		ModelId = model,
		Kind = InferenceFailureKind.NotConfigured,
		Detail = "The configured Codex CLI could not be started.",
	};

	private static InferenceProviderFailure Failure(string model, string detail) => new() {
		ModelId = model,
		Kind = InferenceFailureKind.ProviderUnavailable,
		Detail = detail,
	};

	private static InferenceProviderFailure Invalid(string model, string detail) => new() {
		ModelId = model,
		Kind = InferenceFailureKind.InvalidResponse,
		Detail = detail,
	};
}
