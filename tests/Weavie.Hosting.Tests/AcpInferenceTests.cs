using System.Text.Json;
using Weavie.AgentClientProtocol;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpInferenceTests : IDisposable {
	private readonly TempDirectory _workspace = new("weavie-acp-inference-tests");

	public void Dispose() => _workspace.Dispose();

	[Fact]
	public async Task RunsInTheOwningWorkspaceAndRefusesEveryAgentRequest() {
		var result = Assert.IsType<InferenceProviderSuccess>(await Query("ok"));

		Assert.Equal("fake-model", result.ModelId);
		using var output = JsonDocument.Parse(result.OutputJson);
		Assert.Equal("feat/fake-branch", output.RootElement.GetProperty("branch").GetString());
		Assert.Equal(Path.GetFullPath(_workspace.Path), output.RootElement.GetProperty("cwd").GetString());
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
	public async Task AppliesDependentModelEffortAndFastModeControlsBeforeThePrompt() {
		var provider = Provider(
			AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
			["inference", "ok"]);
		var request = Request() with {
			Profile = Profile("opus", "low", InferenceFastMode.On),
		};

		var result = Assert.IsType<InferenceProviderSuccess>(
			await provider.QueryInferenceAsync(request, CancellationToken.None));

		Assert.Equal("opus", result.ModelId);
		using var output = JsonDocument.Parse(result.OutputJson);
		Assert.Equal("opus", output.RootElement.GetProperty("model").GetString());
		Assert.Equal("low", output.RootElement.GetProperty("effort").GetString());
		Assert.True(output.RootElement.GetProperty("fast").GetBoolean());
		Assert.True(output.RootElement.GetProperty("booleanConfigOptions").GetBoolean());
		Assert.Equal(
			["model", "effort", "fast"],
			output.RootElement.GetProperty("mutations").EnumerateArray().Select(value => value.GetString()));
	}

	[Fact]
	public async Task AppliesSelectFastModeFromTheAlternateShippedControlId() {
		var provider = Provider(
			AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
			["inference", "select-fast-mode"]);
		var request = Request() with {
			Profile = Profile(string.Empty, string.Empty, InferenceFastMode.On),
		};

		var result = Assert.IsType<InferenceProviderSuccess>(
			await provider.QueryInferenceAsync(request, CancellationToken.None));

		using var output = JsonDocument.Parse(result.OutputJson);
		Assert.True(output.RootElement.GetProperty("fast").GetBoolean());
		Assert.Equal(
			["fast-mode"],
			output.RootElement.GetProperty("mutations").EnumerateArray().Select(value => value.GetString()));
	}

	[Fact]
	public async Task ExplicitUnavailableProfileFailsWithoutPromptingOrFallingBack() {
		var provider = Provider(
			AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
			["inference", "no-fast"]);
		var request = Request() with {
			Profile = Profile(string.Empty, string.Empty, InferenceFastMode.On),
		};

		var failure = Assert.IsType<InferenceProviderFailure>(
			await provider.QueryInferenceAsync(request, CancellationToken.None));

		Assert.Equal(InferenceFailureKind.NotConfigured, failure.Kind);
		Assert.Contains("Fast Mode", failure.Detail, StringComparison.Ordinal);
	}

	[Fact]
	public async Task UnadvertisedModelValueFailsWithoutProviderFallback() {
		var provider = Provider(
			AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
			["inference", "ok"]);
		var request = Request() with {
			Profile = Profile("missing-model", string.Empty, InferenceFastMode.Inherit),
		};

		var failure = Assert.IsType<InferenceProviderFailure>(
			await provider.QueryInferenceAsync(request, CancellationToken.None));

		Assert.Equal(InferenceFailureKind.NotConfigured, failure.Kind);
		Assert.Contains("missing-model", failure.Detail, StringComparison.Ordinal);
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
		var provider = Provider(_workspace.Combine("does-not-exist"), []);

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
		new AcpControlStore(new LocalFileSystem(), Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))),
		_ => { });

	private InferenceProviderRequest Request() => new() {
		Category = InferenceModelCategory.Utility,
		Profile = Profile(string.Empty, string.Empty, InferenceFastMode.Inherit),
		Workspace = _workspace.Path,
		Prompt = "Propose one branch name.",
		Images = [],
		OutputSchemaJson = "{\"type\":\"object\",\"properties\":{\"branch\":{\"type\":\"string\"}}}",
		MaxOutputBytes = 4096,
	};

	private static InferenceProviderProfile Profile(
		string model,
		string effort,
		InferenceFastMode fastMode) => new() {
			Model = model,
			Effort = effort,
			FastMode = fastMode,
		};

	private InferenceProviderRequest RequestWithImage() => Request() with {
		Images = [new InferenceInputImage { Mime = "image/png", Bytes = new byte[] { 1, 2, 3, 4 } }],
	};
}
