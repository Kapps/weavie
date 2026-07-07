using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.Hosting.Agents.Codex;

/// <summary>Runs fire-and-forget Codex session work and surfaces failures to the native pane.</summary>
internal static class CodexSessionTasks {
	public static void Run(Func<Task> action, Action<AgentPaneMessage> emit) {
		ArgumentNullException.ThrowIfNull(action);
		ArgumentNullException.ThrowIfNull(emit);
		_ = RunAsync(action, emit);
	}

	private static async Task RunAsync(Func<Task> action, Action<AgentPaneMessage> emit) {
		try {
			await action().ConfigureAwait(false);
		} catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException) {
			emit(new AgentPaneMessage {
				Type = "error",
				ProviderId = "codex",
				Text = ex.Message,
				Status = "error",
			});
		}
	}
}
