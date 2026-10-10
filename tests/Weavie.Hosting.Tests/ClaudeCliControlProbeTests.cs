using Weavie.Hosting.Inference;
using Weavie.Hosting.Inference.Claude;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class ClaudeCliControlProbeTests {
	private const string Catalog = """
		{"type":"system","subtype":"init"}
		{"type":"control_response","response":{"subtype":"success","request_id":"weavie-probe","response":{"fast_mode_state":"on","models":[{"value":"default","displayName":"Default (recommended)"},{"value":"opus","displayName":"Opus 5.5","description":"For complex work","supportedEffortLevels":["low","xhigh"],"supportsFastMode":true},{"value":"haiku","displayName":"Haiku 5.5","supportedEffortLevels":["low","high"]},{"value":"claude-haiku-4-5","displayName":"Haiku 4.5"}]}}}
		""";

	[Fact]
	public async Task ReadsTheCatalogFromTheInitializeHandshakeWithoutAModelTurn() {
		var runner = new Runner(new AgentCliProcessResult(0, Catalog));

		var controls = await ClaudeCliControlProbe.ProbeAsync(runner, "claude", "haiku", "low", "", CancellationToken.None);

		var request = Assert.Single(runner.Requests);
		Assert.Contains("--input-format", request.Arguments);
		Assert.Contains("\"subtype\":\"initialize\"", request.StandardInput, StringComparison.Ordinal);
		var model = controls.Defaults.Single(axis => axis.Category == "model");
		Assert.Equal("Haiku 5.5", model.ValueLabel);
		Assert.Equal(["opus", "haiku", "claude-haiku-4-5"], model.Options.Select(option => option.Id));
		Assert.Equal("For complex work", model.Options[0].Description);
		Assert.Equal(["Low", "High"], controls.Selected.Single(axis => axis.Category == "thought_level").Options.Select(option => option.Label));
		Assert.DoesNotContain(controls.Selected, axis => axis.Id == "fast");
	}

	[Fact]
	public void TheConfiguredModelSelectsItsOwnEffortsAndFastMode() {
		var controls = ClaudeCliControlProbe.Parse(Catalog, "haiku", "low", "opus");

		var effort = controls.Selected.Single(axis => axis.Category == "thought_level");
		Assert.Equal(["low", "xhigh"], effort.Options.Select(option => option.Id));
		Assert.Equal("low", effort.Value);
		Assert.Equal("true", controls.Selected.Single(axis => axis.Id == "fast").Value);
	}

	[Fact]
	public void AModelWithoutEffortLevelsOffersNoEffort() {
		var controls = ClaudeCliControlProbe.Parse(Catalog, "haiku", "low", "claude-haiku-4-5");

		Assert.DoesNotContain(controls.Selected, axis => axis.Category == "thought_level");
	}

	[Fact]
	public async Task AFailedHandshakeSaysSo() {
		var runner = new Runner(new AgentCliProcessResult(1, ""));

		var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			ClaudeCliControlProbe.ProbeAsync(runner, "claude", "haiku", "low", "", CancellationToken.None));

		Assert.Contains("code 1", error.Message, StringComparison.Ordinal);
		Assert.Throws<InvalidOperationException>(() => ClaudeCliControlProbe.Parse("{\"type\":\"system\"}", "haiku", "low", ""));
	}

	private sealed class Runner(AgentCliProcessResult result) : IAgentCliProcessRunner {
		public List<AgentCliProcessRequest> Requests { get; } = [];

		public Task<AgentCliProcessResult> RunAsync(AgentCliProcessRequest request, CancellationToken ct) {
			Requests.Add(request);
			return Task.FromResult(result);
		}
	}
}
