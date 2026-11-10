using Xunit;

namespace Weavie.Workflows.Tests;

public sealed class WorkflowPendingEvidenceTests {
	private static WorkflowDecisionPlan Plan(IReadOnlyList<string> paths) => new(
		"Approve - review.md?", [new("approve", "Complete."), new("revise", null)],
		new Dictionary<string, string> { ["documents"] = "- review.md" }, "Reviewed. User feedback: check links.", paths, Path.GetTempPath(), 0.8);

	[Fact]
	public async Task ReadsEveryFileBeforeCallingTheDeciderWithCompletePreparedEvidence() {
		var reads = new List<string>();
		var reader = new Reader(path => {
			reads.Add(path);
			return new WorkflowFileRead.Text(path == "review.md" ? "Complete [linked](unread.md)." : "");
		});
		var decider = new Decider((request, _) => {
			Assert.Equal(["review.md", "empty.md"], reads);
			Assert.Equal("Approve - review.md?", request.Question);
			Assert.Equal(Plan([]).Choices, request.Choices);
			Assert.Equal(Plan([]).Outputs, request.Evidence.Outputs);
			Assert.Equal(Plan([]).Summary, request.Evidence.Summary);
			Assert.Equal([new("review.md", "Complete [linked](unread.md)."), new WorkflowFileEvidence("empty.md", "")], request.Evidence.Files);
			return Task.FromResult<WorkflowDecisionResult>(new WorkflowDecisionResult.Answered("approve", 0.9));
		});
		WorkflowDecisionResult? result = null;
		using var attempt = new WorkflowRunner.PendingDecision(Plan(["review.md", "empty.md"]), reader, decider, (_, answer) => result = answer);
		await attempt.Completion;
		Assert.Equal(new WorkflowDecisionResult.Answered("approve", 0.9), result);
		Assert.Equal(1, decider.Calls);
	}

	[Fact]
	public async Task AnyUnreadableFileDiscardsPartialEvidenceAndNamesTheFailureWithoutCallingTheModel() {
		var reader = new Reader(path => path == "review.md"
			? new WorkflowFileRead.Text("Private body") : new WorkflowFileRead.Unreadable("not readable text"));
		var decider = NoDecider();
		WorkflowDecisionResult? result = null;
		using var attempt = new WorkflowRunner.PendingDecision(Plan(["review.md", "video.mp4"]), reader, decider, (_, answer) => result = answer);
		await attempt.Completion;
		Assert.Equal(new WorkflowDecisionResult.Failed("\"video.mp4\": not readable text"), result);
		Assert.Equal(0, decider.Calls);
	}

	[Fact]
	public async Task CancellationDuringAReadPreventsFurtherReadsAndInference() {
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<WorkflowFileRead>(TaskCreationOptions.RunContinuationsAsynchronously);
		var reads = new List<string>();
		var reader = new Reader(path => {
			reads.Add(path);
			entered.SetResult();
			return release.Task.GetAwaiter().GetResult();
		});
		var decider = NoDecider();
		int resolutions = 0;
		using var attempt = new WorkflowRunner.PendingDecision(Plan(["review.md", "later.md"]), reader, decider, (_, _) => resolutions++);
		try {
			await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			attempt.Dispose();
			attempt.Dispose();
		} finally {
			release.TrySetResult(new WorkflowFileRead.Text("late body"));
		}
		await attempt.Completion;
		Assert.Equal(["review.md"], reads);
		Assert.Equal(0, decider.Calls);
		Assert.Equal(0, resolutions);
	}

	[Fact]
	public async Task RetiringAnAttemptKeepsItsTokenUsableUntilInferenceCompletes() {
		var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		bool cancellationObserved = false;
		var decider = new Decider(async (_, token) => {
			entered.SetResult(token);
			await release.Task;
			using var registration = token.Register(() => cancellationObserved = true);
			token.ThrowIfCancellationRequested();
			return new WorkflowDecisionResult.Answered("approve", 1);
		});
		int resolutions = 0;
		using var attempt = new WorkflowRunner.PendingDecision(Plan([]), new Reader(_ => throw new InvalidOperationException()), decider, (_, _) => resolutions++);
		var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		attempt.Dispose();
		Assert.True(token.IsCancellationRequested);
		release.SetResult();
		await attempt.Completion;
		Assert.True(cancellationObserved);
		Assert.Equal(0, resolutions);
	}

	[Fact]
	public async Task UnexpectedReadCancellationBecomesFailureInsteadOfLeavingTheDecisionPending() {
		WorkflowDecisionResult? result = null;
		using var attempt = new WorkflowRunner.PendingDecision(Plan(["review.md"]),
			new Reader(_ => throw new OperationCanceledException("reader interrupted")), NoDecider(), (_, answer) => result = answer);
		await attempt.Completion;
		Assert.Equal(new WorkflowDecisionResult.Failed("reader interrupted"), result);
	}

	private static Decider NoDecider() => new((_, _) => throw new InvalidOperationException("No model call expected."));

	private sealed class Reader(Func<string, WorkflowFileRead> read) : IWorkflowFileReader {
		public WorkflowFileRead Read(string relativePath, string documentsDirectory) => read(relativePath);
	}

	private sealed class Decider(Func<WorkflowDecisionRequest, CancellationToken, Task<WorkflowDecisionResult>> decide) : IWorkflowDecider {
		public int Calls { get; private set; }
		public Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct) {
			Calls++;
			return decide(request, ct);
		}
	}
}
