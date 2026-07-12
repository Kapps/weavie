using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents.Codex;

public sealed partial class CodexAppServerSession {
	private bool BeginWorkspaceTurn() {
		try {
			EmitFeedback(_context.Events.Observe(new AgentWorkspaceTurnStarting()));
			return true;
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			CompleteWorkspaceTurn();
			EmitChangeTrackingError(ex);
			return false;
		}
	}

	private void CompleteWorkspaceTurn() {
		try {
			EmitFeedback(_context.Events.Observe(new AgentWorkspaceTurnCompleted()));
		} catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
			EmitChangeTrackingError(ex);
		}
	}

	private void EmitChangeTrackingError(Exception ex) =>
		Emit(new AgentPaneMessage {
			Type = "error",
			ProviderId = "codex",
			ThreadId = CurrentThreadId(),
			Summary = "Change tracking failed for this turn",
			Text = ex.Message,
			Status = "error",
		});
}
