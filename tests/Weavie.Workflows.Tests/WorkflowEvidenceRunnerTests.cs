using System.Text.Json;
using System.Threading.Channels;
using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowEvidenceRunnerTests : IDisposable {
	private const string Flow = """
		id: evidence
		description: current evidence
		inputs: { document: files }
		stages:
		  - id: review
		    if: "Ready {{inputs.document}}?"
		    run: { prompt: Review. }
		""";
	private readonly TempDirectory _dir = new("weavie-evidence-runner");
	private readonly Store _store = new();
	private readonly Decider _decider = new();
	private double _confidence = 0.8;

	public WorkflowEvidenceRunnerTests() {
		_dir.WriteFile(".weavie/workflows/evidence.yaml", Flow);
		_dir.WriteFile("review.md", "PRIVATE ORIGINAL BODY");
	}

	public void Dispose() => _dir.Dispose();
	private WorkflowRunner Runner(IWorkflowFileReader files) => new(
		TestWorkflows.Catalog(_dir.Path, _dir.Combine("personal")), _store, _decider, files, new Port(),
		() => 3, () => true, () => _confidence, _dir.Path, TestWorkflows.Words);
	private static JsonElement Inputs() => JsonDocument.Parse("""{"document":["review.md"]}""").RootElement;

	[Fact]
	public async Task ReactivationReadsCurrentEvidenceAndThresholdWithoutPersistingBodies() {
		var reader = new Reader(_ => new WorkflowFileRead.Text(File.ReadAllText(_dir.Combine("review.md"))));
		using (var initial = Runner(reader)) {
			initial.Start("evidence", Inputs());
			var first = await _decider.Calls.Reader.ReadAsync();
			Assert.Equal("PRIVATE ORIGINAL BODY", Assert.Single(first.Request.Evidence.Files).Text);
			Assert.DoesNotContain("PRIVATE ORIGINAL BODY", _store.Text, StringComparison.Ordinal);
			initial.Dispose();
			Assert.True(first.Token.IsCancellationRequested);
		}

		_dir.WriteFile("review.md", "PRIVATE CURRENT BODY");
		_confidence = 0.95;
		using var restored = Runner(reader);
		restored.Activate();
		var current = await _decider.Calls.Reader.ReadAsync();
		Assert.Equal("PRIVATE CURRENT BODY", Assert.Single(current.Request.Evidence.Files).Text);
		current.Answer.SetResult(new WorkflowDecisionResult.Answered("yes", 0.9));
		await Phase(restored, WorkflowPhase.Asking);
		Assert.Contains("0.95", restored.Current!.Asked!.Reason);
		Assert.DoesNotContain("PRIVATE CURRENT BODY", _store.Text, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AFileLostAfterValidationEscalatesWithNoModelCallAndHumanCanContinue() {
		using (var initial = Runner(new Reader(_ => new WorkflowFileRead.Text("original")))) {
			initial.Start("evidence", Inputs());
			await _decider.Calls.Reader.ReadAsync();
		}
		File.Delete(_dir.Combine("review.md"));
		using var restored = Runner(new Reader(_ => new WorkflowFileRead.Unreadable("file no longer exists")));
		restored.Activate();
		await Phase(restored, WorkflowPhase.Asking);
		Assert.Contains("\"review.md\": file no longer exists", restored.Current!.Asked!.Reason);
		Assert.Equal("Ready - review.md?", restored.Current.Asked.Question);
		Assert.Equal(["yes", "no"], restored.Current.Asked.Choices);
		Assert.False(_decider.Calls.Reader.TryRead(out _));
		restored.Answer(restored.Current.Id, "yes", "I reviewed the saved copy.");
		Assert.Equal(WorkflowPhase.Open, restored.Current.Phase);
		Assert.Contains("I reviewed the saved copy.", restored.Current.Kickoff!.Text);
	}

	[Theory]
	[InlineData("cancel")]
	[InlineData("jump")]
	[InlineData("dispose")]
	public async Task ABlockedReadDoesNotHoldTheRunnerLock(string action) {
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<WorkflowFileRead>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var runner = Runner(new Reader(_ => {
			entered.SetResult();
			return release.Task.GetAwaiter().GetResult();
		}));
		var run = runner.Start("evidence", Inputs());
		try {
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			await Task.Run(() => {
				switch (action) {
					case "cancel": runner.Cancel(run.Id); break;
					case "jump": runner.Jump(run.Id, "review"); break;
					case "dispose": runner.Dispose(); break;
				}
			}).WaitAsync(TimeSpan.FromSeconds(10));
			Assert.Equal(action switch {
				"cancel" => WorkflowPhase.Cancelled,
				"jump" => WorkflowPhase.Open,
				_ => WorkflowPhase.Deciding,
			}, runner.Current!.Phase);
		} finally {
			release.TrySetResult(new WorkflowFileRead.Text("late"));
		}
	}

	private static async Task Phase(WorkflowRunner runner, WorkflowPhase phase) {
		var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnChanged() {
			if (runner.Current?.Phase == phase) changed.TrySetResult();
		}
		runner.Changed += OnChanged;
		try {
			OnChanged();
			await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
		} finally {
			runner.Changed -= OnChanged;
		}
	}

	private sealed class Reader(Func<string, WorkflowFileRead> read) : IWorkflowFileReader {
		public WorkflowFileRead Read(string relativePath) => read(relativePath);
	}
	private sealed class Port : IWorkflowRunPort {
		public void Kickoff(WorkflowKickoff turn) { }
		public void Notify(string message) { }
		public string? Refusal(WorkflowDefinition definition) => null;
	}
	private sealed class Store : IWorkflowRunStore {
		public string Text { get; private set; } = WorkflowRunDocument.Write(null);
		public WorkflowRun? Run => WorkflowRunDocument.Read(Text);
		public string? LoadFailure => null;
		public void Save(WorkflowRun? run) => Text = WorkflowRunDocument.Write(run);
	}
	private sealed record Call(WorkflowDecisionRequest Request, CancellationToken Token, TaskCompletionSource<WorkflowDecisionResult> Answer);
	private sealed class Decider : IWorkflowDecider {
		public Channel<Call> Calls { get; } = Channel.CreateUnbounded<Call>();
		public Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct) {
			var answer = new TaskCompletionSource<WorkflowDecisionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
			Calls.Writer.TryWrite(new(request, ct, answer));
			return answer.Task.WaitAsync(ct);
		}
	}
}
