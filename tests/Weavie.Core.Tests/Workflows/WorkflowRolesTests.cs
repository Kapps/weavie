using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.FileSystem;
using Weavie.Core.Workflows;
using Weavie.TestSupport;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Core.Tests.Workflows;

/// <summary>Fills no role: a definition that names one is refused.</summary>
internal sealed class NoWorkflowRoles : IWorkflowRoleResolver {
	public static NoWorkflowRoles Instance { get; } = new();

	public IReadOnlyList<WorkflowAgent> Resolve(WorkflowDefinition definition) =>
		definition.Roles.Count == 0 ? [] : throw new WorkflowException("This test fills no roles.");
}

/// <summary>One consultable provider, its model catalog, and an in-memory role store.</summary>
internal sealed class TestRoles : IDisposable {
	public const string StorePath = "/config/workflow-roles.json";

	public TestRoles() {
		Providers.Register(Provider);
		Models = new AgentModelCatalog(Providers);
		Models.Start();
		Store = new WorkflowRoleStore(Files, StorePath);
	}

	public FakeConsultProvider Provider { get; } = new("fake-acp");

	public AgentProviderRegistry Providers { get; } = new();

	public AgentModelCatalog Models { get; }

	public InMemoryFileSystem Files { get; } = new();

	public WorkflowRoleStore Store { get; }

	public void Offer(params string[] models) => Models.Observe(Provider.Info.Id, [FakeConsultProvider.Models(models)], AgentModelSource.Session);

	public void Save(string role, string model) => Save(role, new WorkflowRoleAgent(Provider.Info.Id, model, new Dictionary<string, string>()));

	public void Save(string role, WorkflowRoleAgent agent) => Store.Set(Provider.Info.Id, new Dictionary<string, WorkflowRoleAgent> { [role] = agent });

	public WorkflowRoleResolver Resolver() => new(Store, Provider.Info, Models);

	public WorkflowRoleResolver Resolver(AgentProviderInfo info) => new(Store, info, Models);

	public void Dispose() => Models.Dispose();
}

public sealed class WorkflowRolesTests : IDisposable {
	private readonly TestRoles _roles = new();

	private readonly TempDirectory _dir = new("weavie-workflow-roles");

	// The built-in spec-driven asks for planner, then builder.
	private WorkflowDefinition Definition => new WorkflowCatalog(_dir.Path, _dir.Combine("personal"), _dir.Combine("repository"))
		.List().Single(entry => entry.Id == "spec-driven").Definition!;

	public void Dispose() {
		_roles.Dispose();
		_dir.Dispose();
	}

	private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

	private string Run(string args) => WorkflowRoleCommand.Run(Json(args), _roles.Store, _roles.Providers);

	[Fact]
	public void TheStoreRoundTripsAndNeverResetsAFileItCantUse() {
		_roles.Save("planner", new WorkflowRoleAgent("fake-acp", "alpha", new Dictionary<string, string> { ["effort"] = "high" }));
		var reloaded = new WorkflowRoleStore(_roles.Files, TestRoles.StorePath);
		Assert.Equal("high", reloaded.Find("fake-acp", "planner")!.Options["effort"]);
		Assert.Equal([("planner", "fake-acp", "alpha")], reloaded.All().Select(r => (r.Role, r.Provider, r.Agent.Model)));
		Assert.True(reloaded.Clear("fake-acp", "planner"));
		Assert.False(reloaded.Clear("fake-acp", "planner"));

		foreach (string text in new[] { "{ broken", """{"version":2,"providers":{}}""", """{"version":1,"providers":{},"extra":1}""",
			"""{"version":1,"providers":{"p":{"Planner":{"provider":"p","model":"m","options":{}}}}}""" }) {
			var files = new InMemoryFileSystem([new(TestRoles.StorePath, text)]);
			var store = new WorkflowRoleStore(files, TestRoles.StorePath);
			Assert.Contains(TestRoles.StorePath, Assert.Throws<WorkflowException>(() => store.All()).Message, StringComparison.Ordinal);
			Assert.Equal(text, files.ReadAllText(TestRoles.StorePath));
		}
	}

	[Fact]
	public void ASavedRoleIsUsedAsSavedAndAGoneModelIsAGapNeverASubstitute() {
		_roles.Save("planner", "alpha");
		_roles.Save("builder", "gone");

		// While the models aren't known, a saved role is trusted until its step opens.
		Assert.Equal(["planner", "builder"], _roles.Resolver().Resolve(Definition).Select(agent => agent.Name));

		_roles.Offer("alpha", "beta");
		var read = _roles.Resolver().Read(Definition);
		Assert.Equal(["planner"], read.Agents.Select(agent => agent.Name));
		Assert.Equal([new WorkflowRoleGap("builder", "gone")], read.Unfilled);
		Assert.Null(read.Refusal);
		Assert.Equal(
			"Workflow spec-driven can't start: role \"builder\" is saved as model \"gone\", which Fake fake-acp no longer offers (it offers alpha, beta). "
				+ "Fill a role with weavie.workflow.setRole: role, provider \"fake-acp\", agent.",
			Assert.Throws<WorkflowException>(() => _roles.Resolver().Resolve(Definition)).Message);
	}

	[Fact]
	public void AnUnfilledRoleIsRefusedAndAProviderThatCantChooseAModelSaysSo() {
		_roles.Offer("alpha", "beta");
		Assert.Contains(
			"role \"planner\" has no agent for fake-acp sessions; role \"builder\" has no agent for fake-acp sessions.",
			Assert.Throws<WorkflowException>(() => _roles.Resolver().Resolve(Definition)).Message, StringComparison.Ordinal);

		_roles.Models.Observe("fake-acp", [], AgentModelSource.Session);
		Assert.Equal("Stage \"requirements\" runs on role \"planner\", but Fake fake-acp offers no model to choose.", _roles.Resolver().Read(Definition).Refusal);

		var terminal = _roles.Provider.Info with { Id = "claude", Name = "Claude Code", Capabilities = AgentProviderCapabilities.Terminal };
		Assert.StartsWith("Stage \"requirements\" runs on role \"planner\", but Claude Code runs in a terminal", _roles.Resolver(terminal).Read(Definition).Refusal, StringComparison.Ordinal);
	}

	private IReadOnlyList<WorkflowAgent> Pick(params (string Role, string Model)[] picks) =>
		_roles.Resolver().Picking(picks.ToDictionary(pick => pick.Role, pick => pick.Model)).Resolve(Definition);

	[Fact]
	public void PicksFillRolesAndAreSavedTogetherOnlyWhenTheWholeStartResolves() {
		_roles.Save("planner", new WorkflowRoleAgent("fake-acp", "alpha", new Dictionary<string, string> { ["effort"] = "high" }));
		Assert.Contains("models aren't known yet", Assert.Throws<WorkflowException>(() => Pick(("builder", "beta"))).Message, StringComparison.Ordinal);

		_roles.Offer("alpha", "beta");
		((string, string)[] Picks, string Message)[] refused = [
			([("builder", "beta"), ("planner", "gamma")], "Role \"planner\" can't take model \"gamma\": Fake fake-acp offers alpha, beta."),
			([("builder", "beta"), ("Planner", "beta")], "Workflow spec-driven asks for no role \"Planner\"."),
		];
		foreach (var (picks, message) in refused) {
			Assert.Equal(message, Assert.Throws<WorkflowException>(() => Pick(picks)).Message);
			Assert.Null(_roles.Store.Find("fake-acp", "builder"));
		}

		Assert.Equal(["beta", "beta"], Pick(("builder", "beta"), ("planner", "beta")).Select(agent => agent.Model));
		Assert.Equal(("beta", 0), (_roles.Store.Find("fake-acp", "planner")!.Model, _roles.Store.Find("fake-acp", "planner")!.Options.Count));
		Assert.Equal("beta", _roles.Store.Find("fake-acp", "builder")!.Model);
	}

	[Fact]
	public void TheStoreRefusesWhatItCouldNotReadBack() {
		foreach (var (role, agent, message) in new[] {
			("Planner", new WorkflowRoleAgent("fake-acp", "alpha", new Dictionary<string, string>()), "\"Planner\" isn't a role name: use lowercase words joined by hyphens."),
			("planner", new WorkflowRoleAgent("fake-acp", "", new Dictionary<string, string>()), "A workflow role needs an agent provider and model."),
		}) {
			Assert.Equal(message, Assert.Throws<WorkflowException>(() => _roles.Save(role, agent)).Message);
		}
		Assert.Empty(new WorkflowRoleStore(_roles.Files, TestRoles.StorePath).All());
	}

	[Fact]
	public void TheCommandSetsAndClearsARoleAndRefusesBadArguments() {
		Assert.Equal(
			"planner in fake-acp sessions is now alpha (effort high, fast true).",
			Run("""{"role":"planner","provider":"fake-acp","agent":{"provider":"fake-acp","model":"alpha","options":{"effort":"high","fast":true}}}"""));
		Assert.Equal("true", _roles.Store.Find("fake-acp", "planner")!.Options["fast"]);
		Assert.Equal("Cleared planner for fake-acp sessions.", Run("""{"role":"planner","provider":"fake-acp","clear":true}"""));

		foreach (var (args, message) in new[] {
			("""{"provider":"fake-acp","clear":true}""", "'role' is required."),
			("""{"role":"Planner","provider":"fake-acp","clear":true}""", "\"Planner\" isn't a role name: use lowercase words joined by hyphens."),
			("""{"role":"planner","provider":"nope","clear":true}""", "No agent provider 'nope' is registered; the providers are fake-acp."),
			("""{"role":"planner","provider":"fake-acp"}""", "weavie.workflow.setRole takes exactly one of 'agent' or 'clear'."),
			("""{"role":"planner","provider":"fake-acp","agent":{"model":"alpha"}}""", "'agent' needs 'provider' and 'model'."),
			("""{"role":"planner","provider":"fake-acp","clear":true}""", "No planner is saved for fake-acp sessions."),
		}) {
			Assert.Equal(message, Assert.Throws<WorkflowException>(() => Run(args)).Message);
		}
	}

	[Fact]
	public async Task SettledWaitsOutAProbeAndAnswersAtOnceOtherwise() {
		Assert.Null(await _roles.Models.SettledAsync("unknown", CancellationToken.None));
		var settled = _roles.Models.SettledAsync("fake-acp", CancellationToken.None);
		Assert.False(settled.IsCompleted);

		await _roles.Provider.CompleteProbe(0, FakeConsultProvider.Models("alpha", "beta"));

		var entry = await settled.WaitAsync(TimeSpan.FromSeconds(10));
		Assert.Equal(["alpha", "beta"], entry!.Models.Select(model => model.Id));
	}
}
