using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Core.Tests.Agents;

/// <summary>The model catalog refreshes only on its triggers, and the consult service resolves providers loudly.</summary>
public sealed class AgentConsultationTests : IDisposable {
	private readonly AgentProviderRegistry _providers = new();
	private readonly FakeConsultProvider _codex = new("codex");
	private readonly AgentModelCatalog _models;

	public AgentConsultationTests() {
		_providers.Register(new TerminalProvider());
		_providers.Register(_codex);
		_models = new AgentModelCatalog(_providers);
	}

	public void Dispose() => _models.Dispose();

	[Fact]
	public async Task StartProbesEveryConsultableProviderAndLandsItsModels() {
		_models.Start();

		Assert.Equal(AgentModelStatus.Probing, _models.Find("codex")!.Status);
		Assert.Null(_models.Find("claude"));
		await _codex.CompleteProbe(0, FakeConsultProvider.Models("gpt", "astra"));

		var entry = _models.Find("codex")!;
		Assert.Equal(AgentModelStatus.Ready, entry.Status);
		Assert.Equal(["gpt", "astra"], entry.Models.Select(model => model.Id));
		Assert.Equal(AgentModelSource.Probe, entry.Source);
	}

	[Fact]
	public async Task AFailedRefreshReplacesTheReadySnapshot() {
		_models.Start();
		await _codex.CompleteProbe(0, FakeConsultProvider.Models("astra"));
		Assert.Equal(AgentModelStatus.Ready, _models.Find("codex")!.Status);

		_providers.ReplaceAll(_providers.Providers);
		await _codex.FailProbe(1, "Fake codex requires authentication.");

		var entry = _models.Find("codex")!;
		Assert.Equal(AgentModelStatus.Failed, entry.Status);
		Assert.Empty(entry.Models);
		Assert.Equal("Fake codex requires authentication.", entry.Error);
	}

	[Fact]
	public async Task ASlowProbeCannotOverwriteANewerLiveObservation() {
		_models.Start();
		_models.Observe("codex", [FakeConsultProvider.Models("astra")], AgentModelSource.Session);

		await _codex.CompleteProbe(0, FakeConsultProvider.Models("stale"));

		var entry = _models.Find("codex")!;
		Assert.Equal(AgentModelSource.Session, entry.Source);
		Assert.Equal(["astra"], entry.Models.Select(model => model.Id));
	}

	[Fact]
	public void ACatalogChangeCancelsInFlightProbesAndDropsRemovedProviders() {
		_models.Start();

		_providers.ReplaceAll([new TerminalProvider()]);

		Assert.True(_codex.Probes.Single().Token.IsCancellationRequested);
		Assert.Null(_models.Find("codex"));
	}

	[Fact]
	public async Task ConsultingAnUnknownProviderPointsAtListAgents() {
		var outcome = await new AgentConsultation(_providers, _models).ConsultAsync("gemini", Request(), CancellationToken.None);

		Assert.Contains("listAgents", Assert.IsType<AgentConsultFailure>(outcome).Detail, StringComparison.Ordinal);
	}

	[Fact]
	public async Task TerminalProvidersAreListedButNotConsultable() {
		var consultation = new AgentConsultation(_providers, _models);

		var roster = consultation.Roster();
		var outcome = await consultation.ConsultAsync("claude", Request(), CancellationToken.None);

		Assert.NotNull(roster.Single(entry => entry.Provider.Id == "claude").Unconsultable);
		Assert.Null(roster.Single(entry => entry.Provider.Id == "codex").Unconsultable);
		Assert.Contains("can't be consulted", Assert.IsType<AgentConsultFailure>(outcome).Detail, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AFailedConsultStillRecordsTheControlsItSaw() {
		_models.Start();
		_codex.Consult = (request, _) => Task.FromResult<AgentConsultOutcome>(new AgentConsultFailure {
			ModelId = request.Model,
			Controls = [FakeConsultProvider.Models("gpt", "astra-2")],
			Detail = "no 'astra'",
		});

		await new AgentConsultation(_providers, _models).ConsultAsync("codex", Request(), CancellationToken.None);

		var entry = _models.Find("codex")!;
		Assert.Equal(AgentModelSource.Consult, entry.Source);
		Assert.Equal(["gpt", "astra-2"], entry.Models.Select(model => model.Id));
	}

	private static AgentConsultRequest Request() => new() { Workspace = "/work", Model = "astra", Prompt = "Review it." };

	private sealed class TerminalProvider : IAgentProvider {
		public AgentProviderInfo Info { get; } = new() {
			Id = "claude",
			Name = "Claude",
			Capabilities = AgentProviderCapabilities.Terminal,
			Available = true,
		};

		public void ClearConversation(string workspace) { }

		public IAgentSession CreateSession(AgentSessionContext context) => throw new NotSupportedException();
	}
}
