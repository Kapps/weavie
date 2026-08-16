using System.Text.Json;
using Weavie.Core.Configuration;
using Weavie.Core.Inference;
using Weavie.Hosting.Inference;
using Weavie.Hosting.Inference.Claude;
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
		Assert.Equal(_dir, runner.Request.WorkingDirectory);
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

	[Fact]
	public async Task CancellationPropagatesAndLeavesTheOwningWorkspaceIntact() {
		SetPath("claude.path", Path.Combine(_dir, "claude"));
		var runner = new RecordingRunner((_, ct) => Task.FromCanceled<AgentCliProcessResult>(ct));
		var provider = new ClaudeCliInference(_settings, runner);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
			provider.QueryInferenceAsync(Request(InferenceModelCategory.Utility), cancellation.Token));

		Assert.Equal(_dir, runner.Request!.WorkingDirectory);
		Assert.True(Directory.Exists(_dir));
		Assert.Equal(1, runner.Calls);
	}

	private void SetPath(string key, string value) =>
		_settings.Set(key, JsonSerializer.SerializeToElement(value));

	private InferenceProviderRequest Request(InferenceModelCategory category) => new() {
		Category = category,
		Workspace = _dir,
		Prompt = "Return one branch name.\n\n{\"prompt\":\"fix webm\"}",
		OutputSchemaJson = "{\"type\":\"object\",\"properties\":{\"branch\":{\"type\":\"string\"}},"
			+ "\"required\":[\"branch\"],\"additionalProperties\":false}",
		MaxOutputBytes = 4096,
	};

	private static string ValueAfter(IReadOnlyList<string> arguments, string flag) {
		int index = arguments.ToList().IndexOf(flag);
		Assert.InRange(index, 0, arguments.Count - 2);
		return arguments[index + 1];
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
