using System.Text.Json;
using Weavie.AgentClientProtocol;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpInferenceTests : IDisposable {
	private readonly string _workspace = Path.Combine(
		Path.GetTempPath(),
		"weavie-acp-inference-tests",
		Guid.NewGuid().ToString("n"));

	public AcpInferenceTests() {
		Directory.CreateDirectory(_workspace);
	}

	public void Dispose() => Directory.Delete(_workspace, recursive: true);

	[Fact]
	public async Task RunsInTheOwningWorkspaceAndRefusesEveryAgentRequest() {
		var result = Assert.IsType<InferenceProviderSuccess>(await Query("ok"));

		Assert.Equal("fake-model", result.ModelId);
		using var output = JsonDocument.Parse(result.OutputJson);
		Assert.Equal("feat/fake-branch", output.RootElement.GetProperty("branch").GetString());
		Assert.Equal(Path.GetFullPath(_workspace), output.RootElement.GetProperty("cwd").GetString());
		Assert.True(output.RootElement.GetProperty("refusedProbe").GetBoolean());
	}

	[Fact]
	public async Task ReportsProviderUsageIncludingCachedInput() {
		var result = Assert.IsType<InferenceProviderSuccess>(await Query("ok"));

		Assert.NotNull(result.Usage);
		Assert.Equal(2, result.Usage!.InputTokens);
		Assert.Equal(26, result.Usage.OutputTokens);
		Assert.Equal(4096, result.Usage.CachedInputTokens);
	}

	[Theory]
	[InlineData("prose")]
	[InlineData("empty")]
	public async Task RejectsAnythingThatIsNotExactlyOneJsonValue(string variant) {
		var failure = Assert.IsType<InferenceProviderFailure>(await Query(variant));

		Assert.Equal(InferenceFailureKind.InvalidResponse, failure.Kind);
	}

	[Fact]
	public async Task StopsAccumulatingPastTheQueryOutputBoundAndSaysSo() {
		var failure = Assert.IsType<InferenceProviderFailure>(await Query("oversize"));

		Assert.Equal(InferenceFailureKind.InvalidResponse, failure.Kind);
		Assert.Contains("output limit", failure.Detail, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ReportsAnAgentRefusalAsItsOwnFailure() {
		var failure = Assert.IsType<InferenceProviderFailure>(await Query("refusal"));

		Assert.Equal(InferenceFailureKind.Refused, failure.Kind);
	}

	[Fact]
	public async Task ReportsAMissingAgentBinaryAsNotConfigured() {
		var provider = Provider(Path.Combine(_workspace, "does-not-exist"), []);

		var failure = Assert.IsType<InferenceProviderFailure>(
			await provider.QueryInferenceAsync(Request(), CancellationToken.None));

		Assert.Equal(InferenceFailureKind.NotConfigured, failure.Kind);
	}

	private Task<InferenceProviderResult> Query(string variant) =>
		Provider(
			AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
			["inference", variant])
			.QueryInferenceAsync(Request(), CancellationToken.None);

	private static AcpAgentProvider Provider(string command, IReadOnlyList<string> arguments) => new(
		new AcpAgentDefinition {
			Id = "fake",
			Name = "Fake ACP",
			Command = command,
			Arguments = arguments,
			Environment = new Dictionary<string, string>(StringComparer.Ordinal),
			Distribution = "custom",
		},
		new AcpSessionStore(new LocalFileSystem(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))),
		_ => { });

	private InferenceProviderRequest Request() => new() {
		Category = InferenceModelCategory.Utility,
		Workspace = _workspace,
		Prompt = "Propose one branch name.",
		OutputSchemaJson = "{\"type\":\"object\",\"properties\":{\"branch\":{\"type\":\"string\"}}}",
		MaxOutputBytes = 4096,
	};
}
