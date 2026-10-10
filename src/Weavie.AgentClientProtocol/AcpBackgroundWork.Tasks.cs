using System.Text.Json;
using Weavie.Core.Agents;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpBackgroundWork {
	private readonly Dictionary<string, BackgroundTask> _tasks = new(StringComparer.Ordinal);

	public void SpawnTask(AcpConversation announcer, JsonElement update) {
		string id = RequiredString(update, "asyncTaskId", "async_task_spawned update");
		string type = RequiredString(update, "taskType", "async_task_spawned update");
		string itemId = "task:" + id;
		var task = new BackgroundTask(announcer, announcer.TurnId(), new AgentBackgroundItem {
			Id = itemId,
			Kind = AgentBackgroundKind.Task,
			Name = RequiredString(update, "name", "async_task_spawned update"),
			Type = type,
			Detail = OptionalString(update, "description"),
			LastActivity = null,
			Usage = null,
			State = AgentBackgroundState.Running,
			CanStop = update.TryGetProperty("canStop", out var canStop) && canStop.ValueKind == JsonValueKind.True,
			StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
			EndedAtMs = null,
			// Claude's workflows report showInTranscript false, so the transcript card follows the task type.
			TranscriptItemId = type == "workflow" ? itemId : null,
		});
		lock (_gate) {
			if (!_tasks.TryAdd(id, task)) throw new AcpProtocolException($"ACP announced async task '{id}' twice.");
		}
		task.PublishCard();
		root.RaiseBackground();
	}

	public void ProgressTask(JsonElement update) {
		lock (_gate) {
			var task = TaskLocked(update);
			task.Item = task.Item with {
				LastActivity = OptionalString(update, "description") ?? OptionalString(update, "summary")
					?? OptionalString(update, "lastToolName") ?? task.Item.LastActivity,
				Usage = ReadUsage(update) ?? task.Item.Usage,
			};
		}
		root.RaiseBackground();
	}

	public void UpdateTaskState(JsonElement update) {
		var state = RequiredString(update, "state", "async_task_state_update") switch {
			"running" => AgentBackgroundState.Running,
			"paused" => AgentBackgroundState.Paused,
			"completed" => AgentBackgroundState.Completed,
			"failed" => AgentBackgroundState.Failed,
			"stopped" => AgentBackgroundState.Stopped,
			var value => throw new AcpProtocolException($"Unsupported ACP async task state '{value}'."),
		};
		BackgroundTask task;
		bool changed;
		lock (_gate) {
			task = TaskLocked(update);
			var previous = task.Item;
			bool terminal = state is not (AgentBackgroundState.Running or AgentBackgroundState.Paused);
			// A finished task stays finished; only a later terminal state corrects which one.
			if (!terminal && !previous.Running) return;
			changed = previous.State != state;
			task.Item = previous with {
				State = state,
				CanStop = !terminal && previous.CanStop,
				EndedAtMs = terminal ? previous.EndedAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null,
				LastActivity = OptionalString(update, "summary") ?? previous.LastActivity,
			};
		}
		if (changed) task.PublishCard();
		root.RaiseBackground();
	}

	/// <summary>The provider id of the task <paramref name="itemId"/> when the user can stop it now.</summary>
	public string? StoppableTask(string itemId) {
		lock (_gate) {
			return _tasks.FirstOrDefault(entry => entry.Value.Item is { Running: true, CanStop: true } item && item.Id == itemId).Key;
		}
	}

	private bool EndTasks(AgentBackgroundState state) {
		BackgroundTask[] ending;
		long ended = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		lock (_gate) {
			ending = [.. _tasks.Values.Where(task => task.Item.Running)];
			foreach (var task in ending) task.Item = task.Item with { State = state, CanStop = false, EndedAtMs = ended };
		}
		foreach (var task in ending) task.PublishCard();
		return ending.Length > 0;
	}

	private int ClearFinishedTasksLocked() {
		string[] finished = [.. _tasks.Where(entry => !entry.Value.Item.Running).Select(entry => entry.Key)];
		foreach (string id in finished) _tasks.Remove(id);
		return finished.Length;
	}

	private BackgroundTask TaskLocked(JsonElement update) {
		string id = RequiredString(update, "asyncTaskId", "async task update");
		return _tasks.TryGetValue(id, out var task) ? task : throw new AcpProtocolException($"ACP updated unknown async task '{id}'.");
	}

	private static AgentBackgroundUsage? ReadUsage(JsonElement update) =>
		update.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
			? new(Count(usage, "totalTokens"), Count(usage, "toolUses"), Count(usage, "durationMs"))
			: null;

	private static long? Count(JsonElement usage, string property) =>
		usage.TryGetProperty(property, out _) ? ReadRequiredNonNegativeInt64(usage, property, "async task usage") : null;

	// A task's transcript card lives in the conversation that announced it, at the turn it started in.
	private sealed class BackgroundTask(AcpConversation announcer, string turnId, AgentBackgroundItem item) {
		public AgentBackgroundItem Item { get; set; } = item;

		public void PublishCard() {
			if (Item.TranscriptItemId is not null) announcer.PublishTaskCard(turnId, Item);
		}
	}
}
