using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Commands;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private void RegisterAgentBackgroundHandlers(HostSession session) =>
		session.Commands.RegisterHandler(CoreCommands.StopBackgroundTask, async (argsJson, _) => {
			try {
				if (session.Agent.Background is not { } background) return CommandResult.Failure("This agent runs no background tasks.");
				string? id = JsonSerializer.Deserialize(argsJson ?? "{}", WireJson.Default.StopBackgroundTaskCommand)?.Id
					?? background.BackgroundWork.Where(item => item is { Running: true, CanStop: true })
						.MaxBy(item => item.StartedAtMs)?.Id
					?? throw new InvalidOperationException("No background task can be stopped.");
				return await background.StopBackgroundTaskAsync(id).ConfigureAwait(false)
					? CommandResult.Success("Stopped the background task.")
					: CommandResult.Failure("The agent did not stop that background task.");
			} catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IOException) {
				return CommandResult.Failure(ex.Message);
			}
		});

	// Actions that stop a session's subagents and background tasks refuse until the caller says to stop them.
	private static CommandResult? BackgroundWorkRefusal(HostSession? session, bool stopBackgroundWork) {
		if (stopBackgroundWork || session?.Agent.Background is not { } background) return null;
		AgentBackgroundItem[] running = [.. background.BackgroundWork.Where(item => item.Running)];
		return running.Length == 0
			? null
			: CommandResult.Failure(
				"This session still has background work running. Stopping it ends that work, and its results won't come back.",
				JsonSerializer.Serialize(
					new BackgroundWorkRefusalData([.. running.Select(item => new BackgroundWorkSummary(
						item.Name, item.Type, item.State.ToString().ToLowerInvariant(), item.StartedAtMs))]),
					WireJson.Default.BackgroundWorkRefusalData));
	}

	private static bool StopsBackgroundWork(string? argsJson) =>
		JsonSerializer.Deserialize(argsJson ?? "{}", WireJson.Default.BackgroundWorkConsent)?.StopBackgroundWork == true;

	internal sealed record StopBackgroundTaskCommand(string? Id);

	internal sealed record BackgroundWorkConsent(bool? StopBackgroundWork);

	internal sealed record BackgroundWorkRefusalData(IReadOnlyList<BackgroundWorkSummary> BackgroundWork);

	internal sealed record BackgroundWorkSummary(string Name, string Type, string State, long StartedAtMs);
}
