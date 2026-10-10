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

	internal sealed record StopBackgroundTaskCommand(string? Id);
}
