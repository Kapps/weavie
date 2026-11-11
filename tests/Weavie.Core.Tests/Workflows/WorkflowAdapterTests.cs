using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Weavie.Core.Workflows;
using Weavie.Workflows;
using Xunit;

namespace Weavie.Core.Tests.Workflows;

public sealed class WorkflowAdapterTests : IDisposable {
	private const string Flow = """
		id: flow
		description: test flow
		stages:
		  - id: a
		    run: { prompt: "do a" }
		""";

	private readonly TempDirectory _dir = new("weavie-workflow-adapters");

	private readonly SettingsStore _settings;

	public WorkflowAdapterTests() {
		_dir.WriteFile("worktree/.weavie/workflows/flow.yaml", Flow);
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
	}

	public void Dispose() {
		_settings.Dispose();
		_dir.Dispose();
	}

	private string StorePath => _dir.Combine("run.json");

	private WorkflowCatalog Catalog() => new(_dir.Combine("worktree"), _dir.Combine("personal"), WeaviePaths.RepositoryWorkflowsDir(_dir.Combine("worktree")));

	private WorkflowRunner Runner() {
		var runner = new WorkflowRunner(
			Catalog(), NoWorkflowRoles.Instance, new WorkflowRunStore(new LocalFileSystem(), StorePath), new NoDecider(), new WorkflowFileReader(new LocalFileSystem()), new NoPort(),
			WeavieWorkflows.Environment(_settings, _dir.Combine("worktree"), id => _dir.Combine("documents", id)));
		runner.Activate();
		return runner;
	}

	private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

	[Fact]
	public void StorePersistsTheRunAcrossInstances() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		Assert.Equal(run.Id, new WorkflowRunStore(new LocalFileSystem(), StorePath).Run?.Id);
	}

	[Fact]
	public void FailedRunSaveDoesNotPublishOrReplaceTheCurrentRun() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		int changes = 0;
		runner.Changed += () => changes++;
		File.Delete(StorePath);
		Directory.CreateDirectory(StorePath);
		Assert.Throws<WorkflowException>(() => runner.Cancel(run.Id));
		Assert.Equal(run, runner.Current);
		Assert.Equal(0, changes);
	}

	[Fact]
	public void UnreadableStoreIsSetAsideAndReported() {
		File.WriteAllText(StorePath, "{not json");
		using var runner = Runner();
		Assert.Null(runner.Current);
		Assert.Contains("couldn't be read", runner.Problem);
		Assert.True(File.Exists(StorePath + ".bad"));
		runner.Start("flow", Json("{}"));
		Assert.Null(runner.Problem);
	}

	[Fact]
	public void SessionRegistryRoutesToItsRunnerAndErrorsWithoutARun() {
		using var runner = Runner();
		var registry = new SessionWorkflowRegistry(Catalog(), runner, new WorkflowRoleStore(new InMemoryFileSystem(), "/config/workflow-roles.json"));
		Assert.Null(registry.CaptureStageCall());
		runner.Start("flow", Json("{}"));
		var call = registry.CaptureStageCall();
		call!.Fail("stuck");
		Assert.Equal(WorkflowPhase.Paused, runner.Current!.Phase);
	}

	[Fact]
	public void SummaryCarriesThePauseReasonAndTheQuestionAwaitingTheUser() {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}"));
		var asking = run with {
			Phase = WorkflowPhase.Asking,
			Asked = new WorkflowAsked { Question = "ship it?", Choices = ["yes", "no"], Reason = "the decision confidence 0.7 is below the required 0.8" },
		};
		Assert.Equal("ship it? (the decision confidence 0.7 is below the required 0.8)", WorkflowRunSummary.From(asking, SessionStatus.Idle)!.Reason);
		var paused = run with { Phase = WorkflowPhase.Paused, PausedReason = "stuck" };
		Assert.Equal("stuck", WorkflowRunSummary.From(paused, SessionStatus.Idle)!.Reason);
		Assert.Equal("", WorkflowRunSummary.From(run, SessionStatus.Idle)!.Agent);
		var hold = new WorkflowHold { Agent = "builder", Stage = "a", Own = new() { ["model"] = "alpha" }, Held = [] };
		Assert.Equal("", WorkflowRunSummary.From(run with { Hold = hold }, SessionStatus.Idle)!.Agent);
		hold.Held["model"] = "beta";
		Assert.Equal("builder", WorkflowRunSummary.From(run with { Hold = hold }, SessionStatus.Idle)!.Agent);
		Assert.Equal("", WorkflowRunSummary.From(run with { Stage = "b", Hold = hold }, SessionStatus.Working)!.Agent);
	}

	[Theory]
	[InlineData(WorkflowPhase.Open, SessionStatus.Working, WorkflowRunState.Working)]
	[InlineData(WorkflowPhase.Open, SessionStatus.Starting, WorkflowRunState.Working)]
	[InlineData(WorkflowPhase.Open, SessionStatus.Idle, WorkflowRunState.WaitingOnYou)]
	[InlineData(WorkflowPhase.Open, SessionStatus.NeedsInput, WorkflowRunState.WaitingOnYou)]
	[InlineData(WorkflowPhase.Open, SessionStatus.Waiting, WorkflowRunState.Watching)]
	[InlineData(WorkflowPhase.Open, SessionStatus.Error, WorkflowRunState.Stalled)]
	[InlineData(WorkflowPhase.Asking, SessionStatus.Idle, WorkflowRunState.NeedsAnswer)]
	[InlineData(WorkflowPhase.Deciding, SessionStatus.Idle, WorkflowRunState.Deciding)]
	[InlineData(WorkflowPhase.Paused, SessionStatus.Working, WorkflowRunState.Paused)]
	public void SummaryCombinesPhaseAndSessionStatus(WorkflowPhase phase, SessionStatus status, WorkflowRunState state) {
		using var runner = Runner();
		var run = runner.Start("flow", Json("{}")) with {
			Phase = phase,
			Asked = new WorkflowAsked { Question = "q", Choices = ["a", "b"], Reason = "" },
		};
		Assert.Equal(state, WorkflowRunSummary.From(run, status)!.State);
		Assert.Null(WorkflowRunSummary.From(run with { Phase = WorkflowPhase.Done }, status));
	}

	[Fact]
	public void AKickoffBecomesAnAgentTurnOfItsKind() {
		var skill = new WorkflowKickoff { Context = WorkflowContext.Continue, Text = "/spec x", Command = "spec", Opens = "spec" };
		var turn = skill.Submission([new WorkflowTurnImage("/documents/prompt/r-1/image-1.png", "image/png")]);
		Assert.Equal(("/spec x", "spec", AgentTurnSubmissionKind.ProviderCommand), (turn.Text, turn.CommandName, turn.Kind));
		Assert.Equal(("/documents/prompt/r-1/image-1.png", "image/png"), (turn.Attachments[0].Path, turn.Attachments[0].Mime));
		Assert.Equal(AgentTurnSubmissionKind.Prompt, WorkflowKickoff.Prompt("go").Submission([]).Kind);
	}

	[Fact]
	public void ATerminalAgentTakesACommandBeforeItsImagesAndAPromptAfterThem() {
		WorkflowTurnImage[] images = [new("/documents/prompt/r-1/image-1.png", "image/png")];

		Assert.Equal(["/spec x", images[0].Path],
			new WorkflowKickoff { Context = WorkflowContext.Continue, Text = "/spec x", Command = "spec", Opens = "spec" }.Submission(images).TerminalParts());
		Assert.Equal([images[0].Path, "go"], WorkflowKickoff.Prompt("go").Submission(images).TerminalParts());
	}

	private sealed class NoDecider : IWorkflowDecider {
		public Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct) =>
			throw new InvalidOperationException("No decision expected.");
	}

	private sealed class NoPort : IWorkflowRunPort {
		public void Kickoff(WorkflowKickoff turn, IReadOnlyList<WorkflowTurnImage> images) {
		}

		public void Notify(string message) {
		}

		public string? Refusal(WorkflowLaunch launch) => null;
	}
}
