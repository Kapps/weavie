namespace Weavie.Workflows;

public sealed partial class WorkflowRunner {
	internal sealed class PendingDecision : IDisposable {
		private readonly CancellationTokenSource _cts = new();
		private readonly Lock _lifetime = new();
		private bool _finished;

		public PendingDecision(WorkflowDecisionPlan plan, IWorkflowFileReader files, IWorkflowDecider decider, Action<PendingDecision, WorkflowDecisionResult> resolve) {
			MinimumConfidence = plan.MinimumConfidence;
			var token = _cts.Token;
			Completion = Task.Run(async () => {
				try {
					WorkflowDecisionResult result;
					try {
						token.ThrowIfCancellationRequested();
						result = await PrepareAndDecideAsync(plan, files, decider, token).ConfigureAwait(false);
					} catch (OperationCanceledException) when (token.IsCancellationRequested) {
						return;
					} catch (Exception ex) {
						result = new WorkflowDecisionResult.Failed(ex.Message);
					}

					resolve(this, result);
				} finally {
					lock (_lifetime) {
						_finished = true;
						_cts.Dispose();
					}
				}
			});
		}

		private static async Task<WorkflowDecisionResult> PrepareAndDecideAsync(WorkflowDecisionPlan plan, IWorkflowFileReader reader, IWorkflowDecider decider, CancellationToken token) {
			var files = new List<WorkflowFileEvidence>();
			foreach (string path in plan.FilePaths) {
				token.ThrowIfCancellationRequested();
				var read = reader.Read(path);
				token.ThrowIfCancellationRequested();
				if (read is WorkflowFileRead.Unreadable unreadable) {
					return new WorkflowDecisionResult.Failed($"\"{path}\": {unreadable.Reason}");
				}
				files.Add(new WorkflowFileEvidence(path, ((WorkflowFileRead.Text)read).Contents));
			}
			token.ThrowIfCancellationRequested();
			var request = new WorkflowDecisionRequest(plan.Question, plan.Choices, new WorkflowDecisionEvidence(plan.Outputs, plan.Summary, files));
			return await decider.DecideAsync(request, token).ConfigureAwait(false);
		}

		public Task Completion { get; }

		public double MinimumConfidence { get; }

		public void Dispose() {
			lock (_lifetime) {
				if (!_finished) _cts.Cancel();
			}
		}
	}
}
