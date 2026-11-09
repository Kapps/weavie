using System.Globalization;
using System.Net;
using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Configuration;
using Weavie.Core.Credentials;
using Weavie.Core.FileSystem;
using Weavie.Core.Inference.Decisions;
using Weavie.Core.Sessions;
using Weavie.Core.Workflows;
using Weavie.Core.Workspaces;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

// A workflow's model decision routed by the live inference.decisionProvider through a stubbed TypeSafe endpoint.
public sealed class HostCoreDecisionProviderTests {
	private const string SystemOne = "https://api.typesafe.ai/v1/systemone";

	[Fact]
	public async Task TypeSafeAnswersTheDecision_AndALowProbabilityAsksTheUser() {
		await using var host = await StartTypeSafeAsync(yes: 0.6);
		var (session, run) = await ReachTheModelDecisionAsync(host);

		await WaitForRunLineAsync(host, session, line => line?.GetProperty("state").GetString() == "needsAnswer"
			&& line.Value.GetProperty("reason").GetString() == "Is v1 polished? (the decision confidence 0.6 is below the required 0.8)");

		var body = JsonDocument.Parse(Assert.Single(host.Http.Bodies)).RootElement;
		Assert.Equal(SystemOne, Assert.Single(host.Http.Requests).RequestUri!.AbsoluteUri);
		Assert.Equal("Is v1 polished?", body.GetProperty("questions").GetProperty("decision").GetProperty("instructions").GetString());
		Assert.Equal(("Bearer", "ts-key"), (host.Http.Requests[0].Headers.Authorization!.Scheme, host.Http.Requests[0].Headers.Authorization!.Parameter));
		session.Workflow.Answer(run, "yes", "");
		await WaitForRunLineAsync(host, session, line => line is null);
	}

	[Fact]
	public async Task SwitchingTheSettingLive_SendsTheNextDecisionToTheAgent() {
		await using var host = await StartTypeSafeAsync(yes: 0.05);
		var (session, _) = await ReachTheModelDecisionAsync(host);
		await Wait.UntilAsync(() => History(host).Count == 1);

		host.Settings.Set(InferenceSettings.DecisionProvider, JsonSerializer.SerializeToElement(DecisionProviderIds.Agent));
		session.Workflow.CompleteStage(Json("{}"), "s");

		await WaitForRunLineAsync(host, session, line => line is null);
		Assert.Single(host.Http.Requests);
		Assert.Equal([DecisionProviderIds.TypeSafe, DecisionProviderIds.Agent], History(host).Select(record => record.Provider));
	}

	[Fact]
	public async Task TheFifthAgentAnsweredDecision_OffersADecisionModelUntilAVendorIsChosen() {
		await using var host = await StartAsync(new DecisionStub("yes", 0.9), repo => {
			CommitFlow(repo);
			var history = new WorkflowDecisionHistory(new LocalFileSystem(), WeaviePaths.WorkspaceDecisionHistoryFile(WorkspaceId.ForPath(repo)));
			for (int i = 0; i < 4; i++) history.Append(DecisionProviderIds.Agent, TimeSpan.FromMilliseconds(1300));
		});
		Assert.Null(DecisionModelCard(host));

		var (session, _) = await ReachTheModelDecisionAsync(host);
		await WaitForRunLineAsync(host, session, line => line is null);

		var card = await Wait.ForAsync(() => DecisionModelCard(host));
		Assert.StartsWith("Your last 5 workflow decisions took ~1.3s each through the agent.", card.GetProperty("body").GetString(), StringComparison.Ordinal);
		host.Settings.Set(InferenceSettings.DecisionProvider, JsonSerializer.SerializeToElement(DecisionProviderIds.TypeSafe));
		await Wait.UntilAsync(() => DecisionModelCard(host) is null);
	}

	private static JsonElement? DecisionModelCard(TestHost host) =>
		host.Bridge.LastEvent("suggestions", "changed")?.GetProperty("items").EnumerateArray()
			.Cast<JsonElement?>().FirstOrDefault(item => item!.Value.GetProperty("id").GetString() == "decisions.model");

	// The agent answers "yes" confidently; TypeSafe answers yes at the given probability.
	private static async Task<TestHost> StartTypeSafeAsync(double yes) {
		var host = await StartAsync(new DecisionStub("yes", 0.9));
		host.Settings.Set(InferenceSettings.DecisionProvider, JsonSerializer.SerializeToElement(DecisionProviderIds.TypeSafe));
		new FileCredentialStore(host.CredentialsDir).Save(DecisionProviderIds.TypeSafe, new CredentialSecret("ts-key"));
		string probabilities = string.Create(CultureInfo.InvariantCulture, $$"""{"yes":{{yes}},"no":{{1 - yes}}}""");
		string reply = """{"model":"jev-1.14.0","answers":{"decision":{"type":"choice","probabilities":""" + probabilities + "}}}";
		host.Http.Responder = request => request.RequestUri!.AbsoluteUri == SystemOne
			? (HttpStatusCode.OK, reply)
			: (HttpStatusCode.NotFound, "{}");
		return host;
	}

	private static async Task<(HostSession Session, string Run)> ReachTheModelDecisionAsync(TestHost host) {
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "wf", Base = "main", AgentProviderId = "structured" })).Ok);
		var session = host.Session("wf");
		var run = session.Workflow.Start("flow", Json("{}"));
		session.Workflow.CompleteStage(Json("""{"note":"v1"}"""), "s");
		session.Workflow.Answer(run.Id, "approve", "");
		session.Workflow.CompleteStage(Json("{}"), "s");
		return (session, run.Id);
	}

	private static IReadOnlyList<WorkflowDecisionRecord> History(TestHost host) => new WorkflowDecisionHistory(
		new LocalFileSystem(), WeaviePaths.WorkspaceDecisionHistoryFile(WorkspaceId.ForPath(host.RepoRoot))).Recent;
}
