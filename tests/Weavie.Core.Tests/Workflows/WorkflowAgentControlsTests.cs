using Weavie.Core.Agents;
using Weavie.Core.Workflows;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Core.Tests.Workflows;

public sealed class WorkflowAgentControlsTests {
	private static readonly AgentProviderInfo Acp = new() {
		Id = "fake-acp",
		Name = "Fake ACP",
		Capabilities = AgentProviderCapabilities.StructuredPane,
		Available = true,
	};

	private static readonly IReadOnlyList<AgentControlAxis> Axes = [
		Axis("mode", "mode", "ask", "ask", "auto"),
		Axis("model", "model", "alpha", "alpha", "beta"),
		Axis("fast", null, "false", "true", "false"),
	];

	private static AgentControlAxis Axis(string id, string? category, string value, params string[] options) => new() {
		Id = id,
		Label = char.ToUpperInvariant(id[0]) + id[1..],
		Category = category,
		Kind = "select",
		Value = value,
		ValueLabel = char.ToUpperInvariant(value[0]) + value[1..],
		Options = [.. options.Select(o => new AgentControlOption { Id = o, Label = char.ToUpperInvariant(o[0]) + o[1..] })],
	};

	private static WorkflowAgent Agent(string? model, params (string Id, string Value)[] options) => new() {
		Name = "builder",
		Provider = "fake-acp",
		Model = model,
		Options = options.ToDictionary(o => o.Id, o => o.Value),
	};

	private static WorkflowDefinition Definition(WorkflowAgent agent) => new() {
		Id = "flow",
		Description = "d",
		Inputs = [],
		Agents = [agent],
		SourceText = "",
		Stages = [
			new WorkflowStage {
				Id = "build", If = null, Then = new WorkflowThen.Sequential(),
				Run = new WorkflowWork { Context = WorkflowContext.Continue, Prompt = WorkflowTemplate.Empty, Skill = null, Args = null, Outputs = [], Agent = "builder", Role = null },
			},
		],
	};

	[Theory]
	[InlineData("gamma", "fast:true", "Stage \"build\" asks for model \"gamma\"; the agent advertises alpha, beta.")]
	[InlineData(null, "mode:auto", "Stage \"build\" sets option \"mode\", a permission mode; a workflow can't change what the agent may do without asking.")]
	[InlineData(null, "model:beta", "Stage \"build\" sets option \"model\", the model; name it as the agent's model instead.")]
	[InlineData("beta", "mode:auto", "Stage \"build\" sets option \"mode\", a permission mode; a workflow can't change what the agent may do without asking.")]
	[InlineData("alpha", "effort:high", "Stage \"build\" sets option \"effort\", which the agent doesn't advertise; it advertises fast.")]
	[InlineData(null, "fast:maybe", "Stage \"build\" sets option \"fast\" to \"maybe\"; the agent advertises true, false for it.")]
	public void RunStartRefusesWhatTheCurrentAxesRuleOut(string? model, string option, string refusal) {
		string[] pair = option.Split(':');
		Assert.Equal(refusal, WorkflowAgentControls.Refusal(Definition(Agent(model, (pair[0], pair[1]))), [], Acp, Axes));
	}

	[Fact]
	public void OptionsOfAnotherModelWaitForTheStageToOpen() {
		var agent = Agent("beta", ("effort", "high"));
		Assert.Null(WorkflowAgentControls.Refusal(Definition(agent), [], Acp, Axes));
		var error = Assert.Throws<AgentControlException>(() => WorkflowAgentControls.CheckOptions("build", agent, Axes));
		Assert.Contains("\"effort\", which the agent doesn't advertise", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void UnreadyControlsLeaveOnlyTheProviderChecks() {
		var agent = Agent("gamma", ("mode", "auto"));
		Assert.Null(WorkflowAgentControls.Refusal(Definition(agent), [], Acp, null));
		Assert.Contains(
			"of provider fake-acp, so workflow flow runs only in fake-acp sessions; this session runs other",
			WorkflowAgentControls.Refusal(Definition(agent), [], Acp with { Id = "other" }, null), StringComparison.Ordinal);
		Assert.Contains(
			"but Fake ACP runs in a terminal",
			WorkflowAgentControls.Refusal(Definition(agent), [], Acp with { Capabilities = AgentProviderCapabilities.Terminal }, null),
			StringComparison.Ordinal);
		Assert.Null(WorkflowAgentControls.Refusal(Definition(agent) with { Stages = [] }, [], Acp with { Id = "other" }, null));
	}

	private static WorkflowDefinition RoleDefinition() {
		var pinned = Definition(Agent(null));
		var stage = pinned.Stages[0];
		return pinned with { Agents = [], Stages = [stage with { Run = stage.Run with { Agent = null, Role = "builder" } }] };
	}

	[Fact]
	public void ARoleFilledStageMeetsThePinnedAgentsRefusalsAndAnUnfilledOneIsLeftToTheResolver() {
		var definition = RoleDefinition();
		Assert.Equal(
			"Stage \"build\" asks for model \"gamma\"; the agent advertises alpha, beta.",
			WorkflowAgentControls.Refusal(definition, [Agent("gamma")], Acp, Axes));
		Assert.Contains(
			"runs on role \"builder\" of provider fake-acp",
			WorkflowAgentControls.Refusal(definition, [Agent("beta")], Acp with { Id = "other" }, null), StringComparison.Ordinal);
		Assert.Null(WorkflowAgentControls.Refusal(definition, [], Acp, Axes));
		Assert.Equal(
			"Stage \"build\" runs on role \"builder\", but Fake ACP runs in a terminal, where a stage can't choose its model; run the workflow in a native agent session.",
			WorkflowAgentControls.Refusal(definition, [], Acp with { Capabilities = AgentProviderCapabilities.Terminal }, null));
	}

	[Fact]
	public void TheModelComesFromTheModelCategory() {
		var agent = Agent("beta", ("fast", "true"));
		Assert.Equal(new AgentControlValue("model", "beta"), WorkflowAgentControls.Model("build", agent, Axes));
		Assert.Null(WorkflowAgentControls.Model("build", Agent(null), Axes));
		WorkflowAgentControls.CheckOptions("build", agent, Axes);
		Assert.Throws<AgentControlException>(() => WorkflowAgentControls.Model("build", agent, [Axes[2]]));
	}

	[Fact]
	public void OwnValuesLeaveOutModesAndTheLineNamesTheModelAndHeldAxes() {
		Assert.Equal(new Dictionary<string, string> { ["model"] = "alpha", ["fast"] = "false" }, WorkflowAgentControls.Own(Axes));
		Assert.Equal("Workflow: build runs on Alpha, Fast False", WorkflowAgentControls.Line("build runs", Axes, ["model", "fast"]));
		Assert.Equal("Workflow: flow ended, back on Alpha", WorkflowAgentControls.Line("flow ended, back", Axes, []));
	}
}
