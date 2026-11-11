namespace Weavie.Workflows.Tests;

internal sealed class RunnerPort : IWorkflowRunPort {
	public List<string> Kickoffs { get; } = [];

	public List<(WorkflowKickoff Turn, IReadOnlyList<WorkflowTurnImage> Images)> Turns { get; } = [];

	public List<string> Notices { get; } = [];

	public void Kickoff(WorkflowKickoff turn, IReadOnlyList<WorkflowTurnImage> images) {
		Kickoffs.Add(turn.Text);
		Turns.Add((turn, images));
	}

	public void Notify(string message) => Notices.Add(message);

	public string? Refuses { get; set; }

	public string? Refusal(WorkflowDefinition definition) => Refuses;
}

internal sealed class MemoryRunStore : IWorkflowRunStore {
	public string Text { get; set; } = WorkflowRunDocument.Write(null);

	public WorkflowRun? Run => WorkflowRunDocument.Read(Text);

	public string? LoadFailure => null;

	public bool FailWrites { get; set; }

	public void Save(WorkflowRun? run) {
		if (FailWrites) throw new WorkflowException("storage unavailable");
		Text = WorkflowRunDocument.Write(run);
	}
}

internal sealed class UnusedFileReader : IWorkflowFileReader {
	public WorkflowFileRead Read(WorkflowFileScope files, string path) => throw new InvalidOperationException("No file read expected.");
}

internal sealed class HeldDecider : IWorkflowDecider {
	public TaskCompletionSource<WorkflowDecisionResult> Next { get; set; } = new();

	public System.Threading.Channels.Channel<CancellationToken> Started { get; } = System.Threading.Channels.Channel.CreateUnbounded<CancellationToken>();

	public Task<WorkflowDecisionResult> DecideAsync(WorkflowDecisionRequest request, CancellationToken ct) {
		var answer = Next.Task;
		Started.Writer.TryWrite(ct);
		return answer.WaitAsync(ct);
	}
}
