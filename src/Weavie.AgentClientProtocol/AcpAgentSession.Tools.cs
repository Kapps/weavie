using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private enum ToolUpdateSource { Initial, Update, Permission }

	private void UpdateTool(JsonElement update, bool initial) {
		lock (_turnTransitionGate) UpdateToolSerialized(update, initial);
	}

	private void UpdateToolSerialized(JsonElement update, bool initial) {
		var tool = MergeTool(update, initial ? ToolUpdateSource.Initial : ToolUpdateSource.Update);
		if (tool.LocallyTerminalized) return;
		lock (_gate) {
			if (tool.Status is "completed" or "failed") _activeTools.Remove(tool.Id);
			else _activeTools.Add(tool.Id);
		}

		bool completed = tool.Status is "completed" or "failed";
		if (tool.MutationMetadataDisclosed || completed) {
			EnsureObservedMutation(tool);
		}
		if (completed) CompleteToolMutations(tool);

		bool settled = false;
		bool dispatchPending = false;
		if (completed) {
			lock (_gate) {
				if (!HasBackgroundWorkLocked() && !_promptActive && _cancelRequested) {
					_cancelRequested = false;
					dispatchPending = true;
				}
				if (!HasBackgroundWorkLocked() && !_promptActive
					&& (_waitingForBackground || tool.StartedObserved)) {
					_waitingForBackground = false;
					settled = true;
				}
			}
			if (settled) {
				Observe(new AgentTurnStopped(WillResume: false));
				CompleteContentStreams();
			}
		}
		PublishTool(tool);
		if (settled) SignalSideTurnSettled();
		if (dispatchPending) DispatchPendingSubmission();
	}

	private AcpToolState MergeTool(JsonElement update, ToolUpdateSource source) {
		string id = RequiredString(update, "toolCallId", "tool call update");
		AcpToolState tool;
		lock (_gate) {
			bool exists = _tools.TryGetValue(id, out tool!);
			if (!exists) {
				if (source == ToolUpdateSource.Update) {
					throw new AcpProtocolException($"ACP updated unknown tool call '{id}'.");
				}
				tool = new AcpToolState { Id = id, TurnId = TurnId() };
				_tools.Add(id, tool);
			}
			if (source == ToolUpdateSource.Initial) {
				if (tool.InitialReported) throw new AcpProtocolException($"ACP tool call '{id}' was started more than once.");
				tool.Title = RequiredString(update, "title", "tool call");
				tool.InitialReported = true;
			}
			if (tool.LocallyTerminalized) return tool;
			if (source != ToolUpdateSource.Permission) tool.NotificationReported = true;
			tool.Title = OptionalString(update, "title") ?? tool.Title;
			if (OptionalString(update, "kind") is { } kind) {
				tool.Kind = kind is "read" or "edit" or "delete" or "move" or "search" or "execute"
					or "think" or "fetch" or "switch_mode" or "other" ? kind : "other";
				tool.MutationMetadataDisclosed = true;
			} else {
				tool.Kind ??= "other";
			}
			if (OptionalString(update, "status") is { } status) {
				tool.Status = status is "pending" or "in_progress" or "completed" or "failed"
					? status
					: throw new AcpProtocolException($"Unsupported ACP tool status '{status}'.");
			} else {
				tool.Status ??= "pending";
			}
			tool.StartedAtMs ??= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			if (update.TryGetProperty("locations", out var locations) && locations.ValueKind == JsonValueKind.Array) {
				tool.Locations = ReadLocations(locations);
				tool.MutationMetadataDisclosed = true;
			}
			if (update.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array) {
				ReadToolContent(content, tool);
				if (tool.Diffs is { Count: > 0 }) tool.MutationMetadataDisclosed = true;
			}
			if (update.TryGetProperty("rawOutput", out var rawOutput)
				&& rawOutput.ValueKind != JsonValueKind.Null) {
				tool.Text = rawOutput.ValueKind == JsonValueKind.String
					? rawOutput.GetString()
					: rawOutput.GetRawText();
			}
			tool.Input = ToolRequestText(update) ?? tool.Input;
		}
		return tool;
	}

	private void CompleteToolMutations(AcpToolState tool) {
		foreach (var mutation in PendingMutationCompletions(tool)) Observe(new AgentToolCompleted(mutation));
	}

	private void SettleToolsForDisposal() {
		TerminalizedTool[] tools;
		lock (_gate) tools = TerminalizeActiveToolsLocked("cancelled");
		ObserveTerminalizedTools(tools);
	}

	private sealed class AcpToolState {
		public required string Id { get; init; }
		public required string TurnId { get; init; }
		public string? Title { get; set; }
		public string? Kind { get; set; }
		public string? Status { get; set; }
		public string? Text { get; set; }
		public string? Input { get; set; }
		public bool InitialReported { get; set; }
		public bool NotificationReported { get; set; }
		public bool LocallyTerminalized { get; set; }
		public IReadOnlyList<AgentPaneLocation>? Locations { get; set; }
		public IReadOnlyList<AgentPaneDiff>? Diffs { get; set; }
		public IReadOnlyList<AgentPaneContent>? Content { get; set; }
		public string? TerminalId { get; set; }
		public long? StartedAtMs { get; set; }
		public bool MutationMetadataDisclosed { get; set; }
		public List<AgentMutation> ObservedMutations { get; } = [];
		public HashSet<string> ObservedMutationKeys { get; } = new(StringComparer.Ordinal);
		public int CompletedMutationCount { get; set; }
		public bool StartedObserved => ObservedMutations.Count > 0;
		public bool CompletedObserved => CompletedMutationCount == ObservedMutations.Count;
	}

}
