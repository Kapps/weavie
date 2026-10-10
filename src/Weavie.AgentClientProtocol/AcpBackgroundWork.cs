using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Sessions;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

/// <summary>The subagents and tasks one root conversation owns, nested spawns and their tasks included.</summary>
internal sealed partial class AcpBackgroundWork(AcpConversation root) {
	private readonly Lock _gate = new();
	private readonly Dictionary<string, Subagent> _subagents = new(StringComparer.Ordinal);
	// Replayed subagents and forgotten finished ones: their states are acknowledged, never rendered.
	private readonly HashSet<string> _settled = new(StringComparer.Ordinal);

	public IReadOnlyList<AgentBackgroundItem> Items {
		get { lock (_gate) return [.. _subagents.Values.Select(subagent => subagent.Item), .. _tasks.Values.Select(task => task.Item)]; }
	}

	/// <summary>The live subagent conversation with the Weavie identity <paramref name="conversationId"/>.</summary>
	public AcpConversation? Conversation(string conversationId) {
		lock (_gate) return _subagents.Values.FirstOrDefault(subagent => !subagent.Retired && subagent.Item.Id == conversationId)?.Conversation;
	}

	/// <summary>A subagent announced while its parent loads history is a replay: its later states are ignored.</summary>
	public void Replayed(string sessionId) {
		lock (_gate) _settled.Add(sessionId);
	}

	public void Spawn(AcpConversation parent, JsonElement update) {
		string sessionId = RequiredString(update, "subagentSessionId", "subagent_spawned update");
		string name = OptionalString(update, "name") ?? "Subagent";
		string task = OptionalString(update, "task") ?? string.Empty;
		long started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		string? parentId = ReferenceEquals(parent, root) ? null : parent.ConversationId;
		var continuation = new AcpConversationState {
			ConversationId = Guid.NewGuid().ToString("N"),
			SessionId = sessionId,
			AnchorTurnNumber = root.TurnNumber,
			InitialPrompt = task,
			TurnNumber = 0,
			GuidanceSent = true,
			PlanTurns = new Dictionary<string, string>(StringComparer.Ordinal),
			Failed = false,
		};
		var conversation = root.CreateSubagent(new AcpConversationSpec(
			AcpConversationHandoff.Fresh(continuation), new AdoptedOpening(sessionId, this, parentId), AcpConversationRole.Subagent));
		conversation.Adopt(parent, name, started);
		lock (_gate) _subagents.Add(sessionId, new Subagent(sessionId, conversation, parentId, new AgentBackgroundItem {
			Id = continuation.ConversationId,
			Kind = AgentBackgroundKind.Subagent,
			Name = name,
			Type = "subagent",
			Detail = task.Length > 0 ? task : null,
			LastActivity = null,
			Usage = null,
			State = AgentBackgroundState.Running,
			CanStop = false,
			StartedAtMs = started,
			EndedAtMs = null,
			TranscriptItemId = continuation.ConversationId,
		}));
		root.RaiseBackground();
	}

	public void Finish(JsonElement update) {
		string sessionId = RequiredString(update, "subagentSessionId", "subagent_state_update");
		string state = RequiredString(update, "state", "subagent_state_update");
		if (state is not ("completed" or "failed" or "cancelled" or "disconnected")) {
			throw new AcpProtocolException($"Unsupported ACP subagent state '{state}'.");
		}
		lock (_gate) {
			if (!_subagents.ContainsKey(sessionId) && !_settled.Contains(sessionId)) {
				throw new AcpProtocolException($"ACP finished an unknown subagent '{sessionId}'.");
			}
		}
		if (End(subagent => subagent.SessionId == sessionId, state)) root.RaiseBackground();
	}

	/// <summary>A subagent whose own runtime failed ends as failed.</summary>
	public void Failed(string conversationId) {
		if (End(subagent => subagent.Item.Id == conversationId, "failed")) root.RaiseBackground();
	}

	/// <summary>Ends every running subagent with the root's own fate.</summary>
	public void End(string state) {
		bool subagents = End(static _ => true, state);
		if (EndTasks(state == "failed" ? AgentBackgroundState.Failed : AgentBackgroundState.Stopped) || subagents) root.RaiseBackground();
	}

	/// <summary>Retires every subagent without publishing; the saved display's recovery settles their cards.</summary>
	public void Abandon() {
		Subagent[] subagents;
		lock (_gate) subagents = [.. _subagents.Values.Where(subagent => !subagent.Retired)];
		foreach (var subagent in subagents) {
			subagent.Retired = true;
			subagent.Conversation.Retire();
		}
	}

	/// <summary>Forgets finished items at the root's next prompt.</summary>
	public void ClearFinished() {
		int removed;
		lock (_gate) {
			string[] finished = [.. _subagents.Where(entry => entry.Value.Retired).Select(entry => entry.Key)];
			foreach (string id in finished) _subagents.Remove(id);
			_settled.UnionWith(finished);
			removed = finished.Length + ClearFinishedTasksLocked();
		}
		if (removed > 0) root.RaiseBackground();
	}

	private bool End(Func<Subagent, bool> scope, string state) {
		Subagent[] ending;
		long ended = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		lock (_gate) {
			ending = [.. _subagents.Values.Where(subagent => subagent.Item.Running && scope(subagent))];
			foreach (var subagent in ending) subagent.Item = subagent.Item with { State = State(state), EndedAtMs = ended };
		}
		foreach (var subagent in ending) subagent.Conversation.EndSubagent(state);
		RetireSettled();
		return ending.Length > 0;
	}

	// A finished subagent keeps its endpoint until its nested subagents finish, since their states arrive on it.
	private void RetireSettled() {
		while (true) {
			Subagent? settled;
			lock (_gate) {
				settled = _subagents.Values.FirstOrDefault(subagent => !subagent.Retired && !subagent.Item.Running
					&& !_subagents.Values.Any(child => !child.Retired && child.ParentId == subagent.Item.Id));
				if (settled is null) return;
				settled.Retired = true;
			}
			settled.Conversation.Observe(new AgentConversationRemoved(settled.Item.Id));
			settled.Conversation.Retire();
		}
	}

	private static AgentBackgroundState State(string state) => state switch {
		"completed" => AgentBackgroundState.Completed,
		"failed" => AgentBackgroundState.Failed,
		_ => AgentBackgroundState.Stopped,
	};

	private sealed class Subagent(string sessionId, AcpConversation conversation, string? parentId, AgentBackgroundItem item) {
		public string SessionId { get; } = sessionId;
		public AcpConversation Conversation { get; } = conversation;
		public string? ParentId { get; } = parentId;
		public AgentBackgroundItem Item { get; set; } = item;
		public bool Retired { get; set; }
	}
}
