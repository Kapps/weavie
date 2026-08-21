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

	[Fact]
	public async Task SendsImagesAsNativeContentBlocks() {
		var provider = Provider(
			AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
			["inference", "image"]);

		var result = Assert.IsType<InferenceProviderSuccess>(
			await provider.QueryInferenceAsync(RequestWithImage(), CancellationToken.None));

		using var output = JsonDocument.Parse(result.OutputJson);
		Assert.Equal("image/png", output.RootElement.GetProperty("imageMime").GetString());
		Assert.Equal("AQIDBA==", output.RootElement.GetProperty("imageData").GetString());
	}

	[Fact]
	public async Task RejectsImagesWhenTheAgentDoesNotAdvertiseThem() {
		var provider = Provider(
			AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
			["inference", "no-image-capability"]);

		var failure = Assert.IsType<InferenceProviderFailure>(
			await provider.QueryInferenceAsync(RequestWithImage(), CancellationToken.None));

		Assert.Equal(InferenceFailureKind.InputRejected, failure.Kind);
		Assert.Contains("does not accept image prompts", failure.Detail, StringComparison.Ordinal);
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
		Images = [],
		OutputSchemaJson = "{\"type\":\"object\",\"properties\":{\"branch\":{\"type\":\"string\"}}}",
		MaxOutputBytes = 4096,
	};

	private InferenceProviderRequest RequestWithImage() => Request() with {
		Images = [new InferenceInputImage { Mime = "image/png", Bytes = new byte[] { 1, 2, 3, 4 } }],
	};
}
