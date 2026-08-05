using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Hosting.Inference;
using Weavie.Hosting.Inference.Claude;
using Weavie.Hosting.Inference.Codex;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AgentCliInferenceTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-cli-inference-tests", Guid.NewGuid().ToString("n"));
	private readonly SettingsStore _settings;

	public AgentCliInferenceTests() {
		Directory.CreateDirectory(_dir);
		_settings = CoreSettings.CreateStore(Path.Combine(_dir, "settings.toml"), enableWatcher: false);
	}

	[Theory]
	[InlineData(InferenceModelCategory.Utility, "haiku", "low")]
	[InlineData(InferenceModelCategory.Reasoning, "sonnet", "medium")]
	public async Task Claude_UsesSelectedProfileWithToolsAndPersistenceDisabled(
		InferenceModelCategory category,
		string model,
		string effort) {
		SetPath("claude.path", Path.Combine(_dir, "claude"));
		var runner = new RecordingRunner((_, _) => Task.FromResult(new AgentCliProcessResult(
			0,
			"{\"is_error\":false,\"session_id\":\"session-1\","
				+ "\"structured_output\":{\"branch\":\"bug/webm\"}}")));
		var provider = new ClaudeCliInference(_settings, runner);

		var result = Assert.IsType<InferenceProviderSuccess>(
			await provider.QueryInferenceAsync(Request(category), CancellationToken.None));

		Assert.Equal("{\"branch\":\"bug/webm\"}", result.OutputJson);
		Assert.Equal("session-1", result.RequestId);
		Assert.Equal(model, result.ModelId);
		Assert.Equal(model, ValueAfter(runner.Request!.Arguments, "--model"));
		Assert.Equal(effort, ValueAfter(runner.Request.Arguments, "--effort"));
		Assert.Contains("--safe-mode", runner.Request.Arguments);
		Assert.Equal(string.Empty, ValueAfter(runner.Request.Arguments, "--tools"));
		Assert.Contains("--strict-mcp-config", runner.Request.Arguments);
		Assert.Contains("--no-session-persistence", runner.Request.Arguments);
		Assert.DoesNotContain("--bare", runner.Request.Arguments);
		Assert.DoesNotContain("--fallback-model", runner.Request.Arguments);
		Assert.Empty(runner.Request.RemoveEnvironment);
		Assert.Contains("fix webm", runner.Request.StandardInput, StringComparison.Ordinal);
		Assert.False(Directory.Exists(runner.Request.WorkingDirectory));
		Assert.Equal(1, runner.Calls);
	}

	[Fact]
	public async Task Claude_RejectsAnEnvelopeWithoutStructuredOutput() {
		SetPath("claude.path", Path.Combine(_dir, "claude"));
		var runner = new RecordingRunner((_, _) => Task.FromResult(
			new AgentCliProcessResult(0, "{\"is_error\":false,\"result\":\"{\\\"branch\\\":\\\"wrong\\\"}\"}")));
		var provider = new ClaudeCliInference(_settings, runner);

		var result = Assert.IsType<InferenceProviderFailure>(
			await provider.QueryInferenceAsync(Request(InferenceModelCategory.Utility), CancellationToken.None));

		Assert.Equal(InferenceFailureKind.InvalidResponse, result.Kind);
		Assert.Equal(1, runner.Calls);
	}

	[Theory]
	[InlineData(InferenceModelCategory.Utility, "gpt-5.6-luna", "low")]
	[InlineData(InferenceModelCategory.Reasoning, "gpt-5.6-sol", "medium")]
	public async Task Codex_UsesEphemeralIsolatedProfileAndCleansTemporaryFiles(
		InferenceModelCategory category,
		string model,
		string effort) {
		string command = Path.Combine(_dir, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
		File.WriteAllText(command, string.Empty);
		SetPath("codex.path", command);
		string? schema = null;
		string[]? initialFiles = null;
		var runner = new RecordingRunner((request, _) => {
			string schemaPath = ValueAfter(request.Arguments, "--output-schema");
			string outputPath = ValueAfter(request.Arguments, "--output-last-message");
			schema = File.ReadAllText(schemaPath);
			initialFiles = [.. Directory.GetFiles(request.WorkingDirectory)
				.Select(path => Path.GetFileName(path)!)
				.Order(StringComparer.Ordinal)];
			File.WriteAllText(outputPath, "{\"branch\":\"bug/webm\"}");
			return Task.FromResult(new AgentCliProcessResult(0, string.Empty));
		});
		var provider = new CodexCliInference(_settings, runner);

		var result = Assert.IsType<InferenceProviderSuccess>(
			await provider.QueryInferenceAsync(Request(category), CancellationToken.None));

		Assert.Equal(model, result.ModelId);
		Assert.Equal("{\"branch\":\"bug/webm\"}", result.OutputJson);
		Assert.Equal(model, ValueAfter(runner.Request!.Arguments, "--model"));
		Assert.Contains($"model_reasoning_effort=\"{effort}\"", runner.Request.Arguments);
		Assert.Contains("--ephemeral", runner.Request.Arguments);
		Assert.Contains("--ignore-user-config", runner.Request.Arguments);
		Assert.Contains("--ignore-rules", runner.Request.Arguments);
		Assert.Contains("--strict-config", runner.Request.Arguments);
		AssertDisabled(runner.Request.Arguments, "apps");
		AssertDisabled(runner.Request.Arguments, "browser_use");
		AssertDisabled(runner.Request.Arguments, "computer_use");
		AssertDisabled(runner.Request.Arguments, "image_generation");
		AssertDisabled(runner.Request.Arguments, "multi_agent");
		AssertDisabled(runner.Request.Arguments, "plugins");
		AssertDisabled(runner.Request.Arguments, "shell_tool");
		AssertDisabled(runner.Request.Arguments, "workspace_dependencies");
		Assert.DoesNotContain("--sandbox", runner.Request.Arguments);
		Assert.Contains("default_permissions=\"weavie-inference\"", runner.Request.Arguments);
		Assert.Contains("permissions.weavie-inference.filesystem.\":root\"=\"deny\"", runner.Request.Arguments);
		Assert.Contains("permissions.weavie-inference.network.enabled=false", runner.Request.Arguments);
		Assert.Contains("tools.view_image=false", runner.Request.Arguments);
		Assert.Contains("tools.web_search=false", runner.Request.Arguments);
		Assert.Contains("web_search=\"disabled\"", runner.Request.Arguments);
		Assert.Equal("never", ValueAfter(runner.Request.Arguments, "--ask-for-approval"));
		Assert.DoesNotContain("app-server", runner.Request.Arguments);
		Assert.Equal(Request(category).OutputSchemaJson, schema);
		Assert.Equal(["output-schema.json"], Assert.IsType<string[]>(initialFiles));
		Assert.False(Directory.Exists(runner.Request.WorkingDirectory));
		Assert.Equal(1, runner.Calls);
	}

	[Fact]
	public async Task Codex_NonzeroExitReturnsAfterOneProcessWithoutRetry() {
		string command = Path.Combine(_dir, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
		File.WriteAllText(command, string.Empty);
		SetPath("codex.path", command);
		var runner = new RecordingRunner((_, _) => Task.FromResult(new AgentCliProcessResult(7, string.Empty)));
		var provider = new CodexCliInference(_settings, runner);

		var result = Assert.IsType<InferenceProviderFailure>(
			await provider.QueryInferenceAsync(Request(InferenceModelCategory.Utility), CancellationToken.None));

		Assert.Equal(InferenceFailureKind.ProviderUnavailable, result.Kind);
		Assert.Equal(1, runner.Calls);
		Assert.False(Directory.Exists(runner.Request!.WorkingDirectory));
	}

	[Fact]
	public async Task CancellationPropagatesAndStillCleansThePrivateDirectory() {
		SetPath("claude.path", Path.Combine(_dir, "claude"));
		var runner = new RecordingRunner((_, ct) => Task.FromCanceled<AgentCliProcessResult>(ct));
		var provider = new ClaudeCliInference(_settings, runner);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			provider.QueryInferenceAsync(Request(InferenceModelCategory.Utility), cancellation.Token));

		Assert.False(Directory.Exists(runner.Request!.WorkingDirectory));
		Assert.Equal(1, runner.Calls);
	}

	private void SetPath(string key, string value) =>
		_settings.Set(key, JsonSerializer.SerializeToElement(value));

	private static InferenceProviderRequest Request(InferenceModelCategory category) => new() {
		OperationId = "branch-name",
		Category = category,
		Instructions = "Return one branch name.",
		InputJson = "{\"prompt\":\"fix webm\"}",
		OutputSchemaJson = "{\"type\":\"object\",\"properties\":{\"branch\":{\"type\":\"string\"}},"
			+ "\"required\":[\"branch\"],\"additionalProperties\":false}",
		OutputSchemaName = "branch-name",
		MaxOutputBytes = 4096,
	};

	private static string ValueAfter(IReadOnlyList<string> arguments, string flag) {
		int index = arguments.ToList().IndexOf(flag);
		Assert.InRange(index, 0, arguments.Count - 2);
		return arguments[index + 1];
	}

	private static void AssertDisabled(IReadOnlyList<string> arguments, string feature) {
		for (int i = 0; i < arguments.Count - 1; i++) {
			if (arguments[i] == "--disable" && arguments[i + 1] == feature) {
				return;
			}
		}

		Assert.Fail($"Expected Codex feature '{feature}' to be disabled.");
	}

	public void Dispose() {
		_settings.Dispose();
		Directory.Delete(_dir, recursive: true);
	}

	private sealed class RecordingRunner(
		Func<AgentCliProcessRequest, CancellationToken, Task<AgentCliProcessResult>> run) : IAgentCliProcessRunner {
		public int Calls { get; private set; }

		public AgentCliProcessRequest? Request { get; private set; }

		public Task<AgentCliProcessResult> RunAsync(AgentCliProcessRequest request, CancellationToken ct) {
			Calls++;
			Request = request;
			return run(request, ct);
		}
	}
}

public sealed class AgentCliProcessRunnerTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-cli-runner-tests", Guid.NewGuid().ToString("n"));

	public AgentCliProcessRunnerTests() {
		Directory.CreateDirectory(_dir);
	}

	[Fact]
	public async Task CancellationKillsTheOneShotProcessAndPropagates() {
		string script = Path.Combine(_dir, "hang.js");
		File.WriteAllText(script, "process.stdin.resume(); setInterval(() => {}, 1000);");
		var runner = new AgentCliProcessRunner();
		using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(new AgentCliProcessRequest {
			Command = TestNode.Command,
			WorkingDirectory = _dir,
			Arguments = [script],
			PathEntries = [],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal),
			RemoveEnvironment = [],
			StandardInput = "input",
			MaxCapturedStdoutBytes = 1024,
			CaptureStdout = true,
		}, cancellation.Token));
	}

	public void Dispose() => Directory.Delete(_dir, recursive: true);
}
