using System.ComponentModel;
using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Editor;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference;

namespace Weavie.Hosting.Inference.Claude;

internal sealed class ClaudeCliInference : IInferenceProvider {
	private const int EnvelopeOverheadBytes = 64 * 1024;
	private readonly SettingsStore _settings;
	private readonly IAgentCliProcessRunner _processes;
	private readonly string _imageRoot;

	public ClaudeCliInference(
		SettingsStore settings,
		IAgentCliProcessRunner processes,
		string imageRoot) {
		ArgumentNullException.ThrowIfNull(settings);
		ArgumentNullException.ThrowIfNull(processes);
		ArgumentException.ThrowIfNullOrWhiteSpace(imageRoot);
		_settings = settings;
		_processes = processes;
		_imageRoot = imageRoot;
	}

	public InferenceProviderInfo InferenceInfo { get; } = new() {
		Categories = [InferenceModelCategory.Utility, InferenceModelCategory.Reasoning],
	};

	public async Task<InferenceProviderResult> QueryInferenceAsync(
		InferenceProviderRequest request,
		CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(request.Profile);
		var profile = Profile(request.Category, request.Profile);
		string? imageDirectory = null;
		try {
			var imagePaths = new List<string>(request.Images.Count);
			if (request.Images.Count > 0) {
				SecureFile.CreateDirectory(_imageRoot);
				imageDirectory = Path.Combine(_imageRoot, Guid.NewGuid().ToString("n"));
				SecureFile.CreateDirectory(imageDirectory);
				for (int index = 0; index < request.Images.Count; index++) {
					var image = request.Images[index];
					if (!PastedImageMedia.TryExtension(image.Mime, out string extension)) {
						throw new InvalidOperationException($"Unsupported inference image type '{image.Mime}'.");
					}
					string path = Path.Combine(imageDirectory, $"image-{index + 1}{extension}");
					SecureFile.WriteAllBytes(path, image.Bytes.ToArray());
					imagePaths.Add(path);
				}
			}

			string command = _settings.RequireString(CoreSettings.ClaudePath);
			if (string.IsNullOrWhiteSpace(command)) {
				return NotConfigured(profile.Model);
			}
			var result = await _processes.RunAsync(new AgentCliProcessRequest {
				Command = command,
				WorkingDirectory = Path.GetFullPath(request.Workspace),
				Arguments = Arguments(request, profile),
				PathEntries = [],
				Environment = new Dictionary<string, string>(StringComparer.Ordinal),
				RemoveEnvironment = [],
				StandardInput = string.Join('\n', imagePaths.Append(request.Prompt)),
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
		} finally {
			if (imageDirectory is not null && Directory.Exists(imageDirectory)) {
				Directory.Delete(imageDirectory, recursive: true);
			}
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

	private static IReadOnlyList<string> Arguments(InferenceProviderRequest request, ClaudeProfile profile) {
		var arguments = new List<string> {
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
		};
		if (request.Profile.FastMode != InferenceFastMode.Inherit) {
			arguments.Add("--settings");
			arguments.Add(JsonSerializer.Serialize(new {
				fastMode = request.Profile.FastMode == InferenceFastMode.On,
			}));
		}
		return arguments;
	}

	private static ClaudeProfile Profile(
		InferenceModelCategory category,
		InferenceProviderProfile configured) {
		var categoryProfile = category switch {
			InferenceModelCategory.Utility => new ClaudeProfile("haiku", "low"),
			InferenceModelCategory.Reasoning => new ClaudeProfile("sonnet", "medium"),
			_ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown inference model category."),
		};
		return new ClaudeProfile(
			configured.Model.Length == 0 ? categoryProfile.Model : configured.Model,
			configured.Effort.Length == 0 ? categoryProfile.Effort : configured.Effort);
	}

	private sealed record ClaudeProfile(string Model, string Effort);
}
