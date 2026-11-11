using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Core.Configuration;
using Weavie.Core.Sessions;
using Weavie.TestSupport;
using Xunit;
using static Weavie.Hosting.Tests.HostCoreWorkflowTests;

namespace Weavie.Hosting.Tests;

// Workflow roles through HostCore: a role is filled from the user's roles for the session's provider before a run
// exists, the composer reads what each provider still needs, and nothing ever runs a role on the session's own model.
public sealed class HostCoreWorkflowRoleTests {
	private static void CommitRoles(string repo) {
		CommitFlow(repo);
		File.WriteAllText(Path.Combine(repo, ".weavie", "workflows", "roles.yaml"), """
			id: roles
			description: build on the builder, then ship on the session's own model
			stages:
			  - id: build
			    run: { prompt: "build it", role: builder }
			  - id: ship
			    run: { prompt: "ship it" }
			""");
		TempGitRepo.Run(repo, "add", ".");
		TempGitRepo.Run(repo, "commit", "-m", "roles");
	}

	// The catalog starts before the session so the session's advertised models land in it.
	private static async Task<(TestHost Host, HostSession Session, FakeStructuredAgentProvider Provider)> StartAsync() {
		var host = await HostCoreWorkflowTests.StartAsync(new DecisionStub("GPT Next", 0.9), CommitRoles);
		host.AgentModels.Start();
		Assert.True((await host.CreateSessionAsync(new NewSessionRequest { Branch = "wf", Base = "main", AgentProviderId = "structured" })).Ok);
		await Eventually(() => host.AgentModels.Find("structured") is { Status: AgentModelStatus.Ready, Models.Count: 3 });
		return (host, host.Session("wf"), Assert.IsType<FakeStructuredAgentProvider>(host.AgentProviders.RequireAvailable("structured")));
	}

	private static Task<CommandResult> SetBuilderAsync(TestHost host, string model) => host.InvokeCommandAsync("wf", CoreCommands.WorkflowSetRole,
		new { role = "builder", provider = "structured", agent = new { provider = "structured", model } }, CancellationToken.None);

	private static async Task<JsonElement> RolesWorkflowAsync(TestHost host, string providerId) =>
		(await host.HostRequestAsync<JsonElement>("sessionCreation", "workflows", new { sourceId = (string?)null, providerId }))
			.GetProperty("workflows").EnumerateArray().Single(workflow => workflow.GetProperty("id").GetString() == "roles");

	[Fact]
	public async Task AFilledRoleRunsItsModelBeforeTheTurnAndAnUnfilledOneLeavesNoRun() {
		var (host, session, provider) = await StartAsync();
		await using var _ = host;

		var refused = await host.InvokeCommandAsync("wf", CoreCommands.WorkflowRun, new { workflow = "roles" }, CancellationToken.None);
		Assert.Equal(
			"Workflow roles can't start: role \"builder\" has no agent for structured sessions. Ask the user which model and options each of "
				+ "these roles should use; never choose for them. Fake structured agent offers GPT Test, GPT Next, GPT Gone. Then save each with "
				+ "weavie.workflow.setRole (role, provider \"structured\", agent) and start the workflow again.",
			refused.Error);
		Assert.Null(session.Workflow.Current);

		Assert.True((await SetBuilderAsync(host, "GPT Next")).Ok);
		Assert.True((await host.InvokeCommandAsync("wf", CoreCommands.WorkflowRun, new { workflow = "roles" }, CancellationToken.None)).Ok);
		await WaitForEchoAsync(host, session, "build it");
		Assert.Equal(["apply model=GPT Next"], provider.ControlChanges);
		await WaitForRunLineAsync(host, session, line => line?.GetProperty("agent").GetString() == "builder");

		// The run keeps what its role resolved to, through a reload too.
		Assert.True((await SetBuilderAsync(host, "GPT Test")).Ok);
		Assert.Equal("GPT Next", session.Workflow.AgentOf("build")!.Model);
		Assert.True((await host.UnloadSessionAsync("wf")).Ok);
		Assert.True((await host.InvokeCommandAsync(host.WorkspaceSession.SlotId, SessionCommands.LoadSession,
			new { id = "wf" }, CancellationToken.None)).Ok);
		Assert.Equal("GPT Next", (await WaitForSessionAsync(host, "wf")).Workflow.AgentOf("build")!.Model);
	}

	[Fact]
	public async Task ANewSessionSavesItsPicksBeforeItStartsAndABadPickLeavesNothing() {
		var (host, _, _) = await StartAsync();
		await using var _ = host;
		static NewSessionRequest Request(string branch, string roles) => new() {
			Branch = branch,
			Base = "main",
			AgentProviderId = "structured",
			Workflow = "roles",
			Roles = Json(roles),
		};

		var unfilled = await host.CreateSessionAsync(Request("unfilled", "{}"));
		var bad = await host.CreateSessionAsync(Request("bad", """{"builder":{"model":"GPT Retired"}}"""));
		var stray = await host.CreateSessionAsync(Request("stray", """{"builder":{"model":"GPT Next"},"Planner":{"model":"GPT Next"}}"""));
		var badInput = await host.CreateSessionAsync(Request("input", """{"builder":{"model":"GPT Next"}}""") with {
			Inputs = JsonSerializer.SerializeToElement(new { bogus = 1 }),
		});
		var loose = await host.CreateSessionAsync(new NewSessionRequest {
			Branch = "loose",
			Base = "main",
			AgentProviderId = "structured",
			Roles = Json("""{"builder":{"model":"GPT Next"}}"""),
		});

		Assert.Contains("role \"builder\" has no agent for structured sessions", unfilled.Error, StringComparison.Ordinal);
		Assert.Equal("Role \"builder\" can't take model \"GPT Retired\": Fake structured agent offers GPT Test, GPT Next, GPT Gone.", bad.Error);
		Assert.Equal("Workflow roles asks for no role \"Planner\".", stray.Error);
		Assert.Contains("unknown input \"bogus\"", badInput.Error, StringComparison.Ordinal);
		Assert.Equal("'roles' needs a 'workflow'.", loose.Error);
		Assert.All(new[] { "unfilled", "bad", "stray", "input", "loose" }, branch => Assert.Null(host.Core.SessionForTest(branch)));
		Assert.Empty(host.WorkflowRoles.All());

		Assert.True((await host.CreateSessionAsync(Request("picked", """{"builder":{"model":"GPT Next","options":{"fast":true}}}"""))).Ok);
		var picked = host.Session("picked").Workflow.AgentOf("build")!;
		Assert.Equal(("GPT Next", "true"), (picked.Model, picked.Options["fast"]));
		Assert.Equal("true", host.WorkflowRoles.Find("structured", "builder")!.Options["fast"]);
		Assert.Empty((await RolesWorkflowAsync(host, "structured")).GetProperty("roles").EnumerateArray());
	}

	[Fact]
	public async Task TheComposerListsWhatEachProviderNeedsAndItsModels() {
		var (host, _, _) = await StartAsync();
		await using var _ = host;

		var needs = await RolesWorkflowAsync(host, "structured");
		Assert.Equal(JsonValueKind.Null, needs.GetProperty("error").ValueKind);
		Assert.Equal(("builder", JsonValueKind.Null), (needs.GetProperty("roles")[0].GetProperty("role").GetString(), needs.GetProperty("roles")[0].GetProperty("problem").ValueKind));
		Assert.StartsWith("Stage \"build\" runs on role \"builder\", but Claude Code runs in a terminal",
			(await RolesWorkflowAsync(host, "claude")).GetProperty("error").GetString(), StringComparison.Ordinal);

		var controls = await host.HostRequestAsync<JsonElement>("sessionCreation", "roleControls", new { providerId = "structured", model = "GPT Next" });
		var models = controls.GetProperty("axes")[0];
		Assert.Equal(("model", "GPT Next"), (models.GetProperty("category").GetString(), models.GetProperty("value").GetString()));
		host.Settings.Set(InferenceSettings.AllowAutomatic, JsonSerializer.SerializeToElement(true));
		Assert.Equal(["GPT Test", "GPT Next", "GPT Gone"], models.GetProperty("options").EnumerateArray().Select(model => model.GetProperty("id").GetString()));
		var suggestion = await host.HostRequestAsync<JsonElement>("sessionCreation", "suggestRole",
			new { sourceId = (string?)null, providerId = "structured", workflow = "roles", role = "builder" });
		Assert.Equal((true, "GPT Next"), (suggestion.GetProperty("applies").GetBoolean(), suggestion.GetProperty("model").GetString()));

		Assert.True((await SetBuilderAsync(host, "GPT Retired")).Ok);
		Assert.Equal("The saved model \"GPT Retired\" is no longer offered.", (await RolesWorkflowAsync(host, "structured")).GetProperty("roles")[0].GetProperty("problem").GetString());
		var stale = await host.HostRequestAsync<JsonElement>("sessionCreation", "suggestRole",
			new { sourceId = (string?)null, providerId = "structured", workflow = "roles", role = "builder" });
		Assert.False(stale.GetProperty("applies").GetBoolean());
	}
}
