using System.Text.Json;
using Weavie.Core.Agents;
using static Weavie.AgentClientProtocol.AcpJson;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	private enum ToolUpdateSource { Initial, Update, Permission }

	private void UpdateTool(JsonElement update, bool initial) {
		lock (_turnTransitionGate) UpdateToolSerialized(update, initial);
	}

	// A tool that hands its work to a subagent session is announced only there; adapters still leak its metadata.
	private bool UnknownMetadataOnly(JsonElement update) {
		lock (_gate) {
			return !_tools.ContainsKey(RequiredString(update, "toolCallId", "tool call update"))
				&& update.EnumerateObject().All(property => property.Name is "sessionUpdate" or "toolCallId" or "_meta");
		}
	}

	private void UpdateToolSerialized(JsonElement update, bool initial) {
		if (!initial && UnknownMetadataOnly(update)) return;
		var tool = MergeTool(update, initial ? ToolUpdateSource.Initial : ToolUpdateSource.Update);
		if (tool.LocallyTerminalized) return;
		// Stopping a backgrounded command kills it, which its agent reports as a failure.
		if (tool is { Backgrounded: true, Status: "failed" } && Background.Stopped(tool.Id)) tool.Status = "cancelled";
		lock (_gate) {
			// A backgrounded tool's liveness is its async task's, so it never holds the turn.
			if (tool.Status is "completed" or "failed" || tool.Backgrounded) _activeTools.Remove(tool.Id);
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
		if (settled) SignalSettled();
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
			// AIR markers are sticky: adapters send them once and drop repeats.
			tool.Backgrounded |= Air(update, "asyncTasks") is { ValueKind: JsonValueKind.Object } tasks
				&& tasks.TryGetProperty("backgrounded", out var backgrounded) && backgrounded.ValueKind == JsonValueKind.True;
			tool.Subagent |= Air(update, "subagent").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
		}
		return tool;
	}

	private TerminalizedTool[] TerminalizeActiveToolsLocked(string status) => TerminalizeToolsLocked(status, static _ => true);

	private TerminalizedTool[] TerminalizeToolsLocked(string status, Func<AcpToolState, bool> scope) {
		var result = new List<TerminalizedTool>();
		foreach (var tool in _tools.Values.Where(tool => (_activeTools.Contains(tool.Id) || !tool.CompletedObserved) && scope(tool))) {
			tool.Status = status;
			tool.LocallyTerminalized = true;
			var completions = PendingMutationCompletions(tool);
			_activeTools.Remove(tool.Id);
			result.Add(new TerminalizedTool(tool, completions));
		}
		return [.. result];
	}

	private bool HasBackgroundWorkLocked() => _activeTools.Count > 0;

	private void ObserveTerminalizedTools(IEnumerable<TerminalizedTool> tools) {
		foreach (var terminalized in tools) {
			foreach (var mutation in terminalized.CompletionMutations) {
				Observe(new AgentToolCompleted(mutation));
			}
		}
	}

	private void PublishTerminalizedToolMessages(IEnumerable<TerminalizedTool> tools) {
		foreach (var terminalized in tools) {
			if (terminalized.Tool.NotificationReported) PublishTool(terminalized.Tool);
		}
	}

	private void EnsureObservedMutation(AcpToolState tool) {
		var mutation = Mutation(tool);
		string key = MutationKey(mutation);
		if (!tool.ObservedMutationKeys.Add(key)) return;
		tool.ObservedMutations.Add(mutation);
		Observe(new AgentToolStarting(mutation));
	}

	private static AgentMutation[] PendingMutationCompletions(AcpToolState tool) {
		var result = tool.ObservedMutations.Skip(tool.CompletedMutationCount).ToArray();
		tool.CompletedMutationCount = tool.ObservedMutations.Count;
		return result;
	}

	private static string MutationKey(AgentMutation mutation) => mutation switch {
		AgentMutation.None => "none",
		AgentMutation.File file => $"file:{file.Path}",
		AgentMutation.Files files => $"files:{string.Join('\n', files.Items.Select(file => file.Path))}",
		_ => throw new InvalidOperationException("Unknown agent mutation type."),
	};

	private void CompleteToolMutations(AcpToolState tool) {
		foreach (var mutation in PendingMutationCompletions(tool)) Observe(new AgentToolCompleted(mutation));
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
		public bool Backgrounded { get; set; }
		public bool Subagent { get; set; }
		public bool Shown { get; set; }
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
