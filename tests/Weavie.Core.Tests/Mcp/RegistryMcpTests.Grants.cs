using System.Text.Json;
using Weavie.Core.Mcp;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Core.Tests;

public sealed partial class RegistryMcpTests {
	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public async Task OnlyTheStageGrantCanCompleteAndItsReplyPrecedesScopeClosure(bool http, bool decision) {
		using var store = NewStore();
		string personal = _dir.Combine("personal");
		_dir.WriteFile("personal/grant.yaml", $$"""
			id: grant
			description: stage credential
			stages:
			  - id: a
			    run: { context: fresh, prompt: complete this }
			    then: {{(decision ? "{ ask: ready?, by: user, choices: { approve: done, revise: a } }" : "done")}}
			""");
		var workflows = NewWorkflows(personal, new GrantPort());
		var runner = _runners[^1];
		using var outputs = JsonDocument.Parse("{}");
		runner.Start("grant", outputs.RootElement);
		var execution = runner.Execution!;
		await using var server = TestMcp.Server(Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true, workflows: workflows);
		server.Start();
		await using var ordinary = server.CreateGrant(() => null, "side");
		await using var stage = server.CreateGrant(execution.Capture, "stage");

		using (var refused = await CompleteWithGrantAsync(server, ordinary, http)) {
			Assert.True(refused.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
			Assert.Contains("does not own an open workflow stage", ToolText(refused), StringComparison.Ordinal);
		}
		using (var response = await PostHttpAsync(server.Port,
			Request(2, "tools/call", """{"name":"completeStage","arguments":{"outputs":{},"summary":"main cannot complete child"}}"""), Token)) {
			using var refused = await TestMcp.ReadResponseAsync(response);
			Assert.Contains("does not own an open workflow stage", ToolText(refused), StringComparison.Ordinal);
		}
		Assert.Equal(WorkflowPhase.Open, runner.Current!.Phase);
		Assert.Same(execution, runner.Execution);
		bool responseOutstandingAtRetirement = false;
		runner.Changed += () => {
			responseOutstandingAtRetirement |= execution.Retired.IsCompleted && !stage.CompletionResponses.IsCompleted;
			if (runner.Current!.Phase == WorkflowPhase.Asking) runner.Answer(runner.Current.Id, "approve", "");
		};
		var closed = CloseAfterResponseAsync(execution, stage);

		using var completed = await CompleteWithGrantAsync(server, stage, http);

		Assert.StartsWith("Complete. The main conversation receives the result", ToolText(completed), StringComparison.Ordinal);
		Assert.Equal(WorkflowPhase.Done, runner.Current!.Phase);
		Assert.True(responseOutstandingAtRetirement);
		await closed.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(stage.CompletionResponses.IsCompletedSuccessfully);
		Assert.False(stage.Live);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task StageToolAdmissionRetainsItsExecutionAndRoundWhenTheRunnerMoves(bool revise) {
		using var store = NewStore();
		string personal = _dir.Combine("personal");
		_dir.WriteFile("personal/grant.yaml", """
			id: grant
			description: captured stage
			stages:
			  - id: a
			    run: { prompt: do a }
			    then:
			      ask: ready?
			      by: user
			      choices: { next: b, revise: a }
			  - id: b
			    run: { prompt: do b }
			    then: done
			""");
		var workflows = NewWorkflows(personal, new GrantPort());
		var runner = _runners[^1];
		using var outputs = JsonDocument.Parse("{}");
		var run = runner.Start("grant", outputs.RootElement);
		var execution = runner.Execution!;
		await using var server = TestMcp.Server(Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", store, registryMode: true, workflows: workflows);
		server.Start();
		int admissions = 0;
		await using var stage = server.CreateGrant(() => {
			admissions++;
			var admitted = execution.Capture();
			// Move between admission and handling, without sleeps or competing network requests.
			if (revise) {
				runner.CompleteStage(outputs.RootElement, "ready");
				runner.Answer(run.Id, "revise", "change it");
			} else {
				runner.Jump(run.Id, "b");
			}
			return admitted;
		}, "stage");

		using var refused = await CompleteWithGrantAsync(server, stage, http: true);

		Assert.Equal(1, admissions);
		Assert.True(refused.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
		Assert.Contains("execution or completion round that has ended", ToolText(refused), StringComparison.Ordinal);
		Assert.Equal(WorkflowPhase.Open, runner.Current!.Phase);
		Assert.Equal(revise ? "a" : "b", runner.Current.Stage);
		Assert.Equal(revise, ReferenceEquals(execution, runner.Execution));
	}

	private static async Task CloseAfterResponseAsync(WorkflowStageExecution execution, CapabilityRegistryGrant grant) {
		Assert.True(await execution.Retired);
		await grant.DisposeAsync();
		await grant.CompletionResponses;
	}

	private static async Task<JsonDocument> CompleteWithGrantAsync(McpServer server, CapabilityRegistryGrant grant, bool http) {
		string request = Request(1, "tools/call", """{"name":"completeStage","arguments":{"outputs":{},"summary":"done"}}""");
		if (http) {
			using var response = await PostHttpAsync(server.Port, request, grant.Credential.Token).WaitAsync(TimeSpan.FromSeconds(5));
			return await TestMcp.ReadResponseAsync(response);
		}
		using var socket = await ConnectBearerAsync(server.Port, grant.Credential.Token);
		await SendAsync(socket, request);
		return await ReceiveAsync(socket);
	}

	private sealed class GrantPort : IWorkflowRunPort {
		public void Kickoff(WorkflowKickoff turn, IReadOnlyList<WorkflowTurnImage> images) { }
		public void Notify(string message) { }
		public string? Refusal(WorkflowDefinition definition) => null;
	}
}
