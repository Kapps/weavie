using System.Text.Json;
using Weavie.Core.Workflows;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>An ACP agent carries the live stage on every turn and completes it through the session registry.</summary>
public sealed class AcpWorkflowTests {
	private const string Flow = """
		id: flow
		description: one stage the fake agent completes itself
		stages:
		  - id: build
		    run:
		      prompt: 'mcp:completeStage:{"outputs":{"pr":"https://example.test/pull/7"},"summary":"opened the PR"}'
		      outputs: { pr: pr }
		""";

	[Fact]
	public async Task StageTurnCarriesTheStageBlockAndCompletesThroughTheRegistry() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		Directory.CreateDirectory(Path.Combine(fixture.Workspace, ".weavie", "workflows"));
		File.WriteAllText(Path.Combine(fixture.Workspace, ".weavie", "workflows", "flow.yaml"), Flow);
		await fixture.StartAsync();

		var run = fixture.Workflow.Start("flow", JsonDocument.Parse("{}").RootElement);
		Assert.True(fixture.WorkflowKickoffs.TryDequeue(out var kickoff));
		fixture.Submit(kickoff.Text);

		await fixture.WaitForMessageAsync(message =>
			message.Text?.StartsWith("completeStage: Complete. The workflow is finished.", StringComparison.Ordinal) == true);
		Assert.Equal(WorkflowPhase.Done, fixture.Workflow.Current!.Phase);
		Assert.Equal("https://example.test/pull/7", fixture.Workflow.Current.Outputs["build"]["pr"].Render());
		string log = Path.Combine(fixture.FakeAcpStateDirectory, "assistant-context.log");
		string context = File.ReadAllText(log);
		Assert.Contains($"Run {run.Id} of workflow \"flow\" is at stage \"build\"", context, StringComparison.Ordinal);

		// A finished run attaches nothing to later turns.
		fixture.Submit("after");
		await fixture.WaitForMessageAsync(message => message.Text == "echo: after");
		Assert.Equal(context, File.ReadAllText(log));
	}

	[Fact]
	public async Task SkillStageRunsTheProviderCommandWithTheStageBlockAfterIt() {
		await using var fixture = AcpAgentSessionFixture.Create(allowAllPermissions: true, persistedSessionId: null);
		Directory.CreateDirectory(Path.Combine(fixture.Workspace, ".weavie", "workflows"));
		File.WriteAllText(Path.Combine(fixture.Workspace, ".weavie", "workflows", "skilled.yaml"), """
			id: skilled
			description: one skill stage
			stages:
			  - id: review
			    run: { skill: review, args: the diff }
			""");
		await fixture.StartAsync();

		var run = fixture.Workflow.Start("skilled", JsonDocument.Parse("{}").RootElement);
		Assert.True(fixture.WorkflowKickoffs.TryDequeue(out var kickoff));
		Assert.True(fixture.Workflow.TryClaimKickoff(kickoff));
		fixture.Deliver(kickoff);

		await fixture.WaitForMessageAsync(message => message.Text == "review command: the diff");
		string context = File.ReadAllText(Path.Combine(fixture.FakeAcpStateDirectory, "assistant-context.log"));
		Assert.Contains($"Run {run.Id} of workflow \"skilled\" is at stage \"review\"", context, StringComparison.Ordinal);
	}
}
