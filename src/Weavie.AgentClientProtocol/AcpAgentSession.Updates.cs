using System.Text;
using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private void HandleNotification(long generation, JsonElement root) {
		lock (_turnTransitionGate) {
			if (!OwnsGeneration(generation)) return;
			HandleNotificationSerialized(root);
		}
	}

	private void HandleNotificationSerialized(JsonElement root) {
		string method = OptionalString(root, "method") ?? string.Empty;
		if (method == "$/cancel_request") {
			CancelClientRequest(root);
			return;
		}
		if (method == "elicitation/complete") {
			if (!root.TryGetProperty("params", out var completion)
				|| completion.ValueKind != JsonValueKind.Object) {
				throw new AcpProtocolException("ACP elicitation completion is missing its parameters.");
			}
			CompleteElicitation(completion);
			return;
		}
		if (method != "session/update") return;
		if (!root.TryGetProperty("params", out var parameters)
			|| parameters.ValueKind != JsonValueKind.Object
			|| !parameters.TryGetProperty("update", out var update)
			|| update.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException("ACP session/update is missing its parameters or update object.");
		}
		string sessionId = OptionalString(parameters, "sessionId")
			?? throw new AcpProtocolException("An ACP session/update notification is missing sessionId.");
		lock (_gate) {
			string? current = _sessionId ?? _openingSessionId;
			if (current is null && _sessionOpening) {
				_openingSessionId = sessionId;
			} else if (current is null) {
				throw new AcpProtocolException("ACP sent a session/update without an active session.");
			} else if (!string.Equals(current, sessionId, StringComparison.Ordinal)) {
				throw new AcpProtocolException(
					$"ACP sent a session/update for '{sessionId}' while '{current}' is active.");
			}
		}
		string kind = RequiredString(update, "sessionUpdate", "session/update notification");
		switch (kind) {
			case "user_message_chunk": EmitContent(update, "user-message-delta", "userMessage"); break;
			case "agent_message_chunk": EmitContent(update, "agent-message-delta", "agentMessage"); break;
			case "agent_thought_chunk": EmitContent(update, "thought-message-delta", "thought"); break;
			case "tool_call": UpdateTool(update, initial: true); break;
			case "tool_call_update": UpdateTool(update, initial: false); break;
			case "plan": EmitPlan(update); break;
			case "available_commands_update": UpdateCommands(update); break;
			case "current_mode_update": UpdateMode(update); break;
			case "config_option_update": UpdateConfig(update); break;
			case "session_info_update": EmitSessionInfo(update); break;
			case "usage_update": EmitUsage(update); break;
			default: throw new AcpProtocolException($"Unsupported ACP session update '{kind}'.");
		}
	}

	private void CancelClientRequest(JsonElement root) {
		if (!root.TryGetProperty("params", out var parameters)
			|| !parameters.TryGetProperty("requestId", out var requestId)
			|| requestId.ValueKind is not (JsonValueKind.String or JsonValueKind.Number)) {
			throw new AcpProtocolException("ACP request cancellation requires a string or numeric requestId.");
		}
		string id = AcpJsonRpcConnection.CanonicalId(requestId);
		if (!_clientRequests.TryGetValue(id, out var state) || !state.TryCancel()) return;
		CancelCompletedClientRequest(state);
	}

	private void EmitContent(JsonElement update, string deltaType, string itemType) {
		if (!update.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException("An ACP content update is missing its content block.");
		}
		if (itemType == "userMessage") {
			lock (_gate) {
				if (!_loadingTranscript) return;
			}
			if (IsInjectedContext(content)) return;
		}
		string? advertisedId = OptionalString(update, "messageId");
		string? advertisedKey = advertisedId is null ? null : $"{itemType}:{advertisedId}";
		string turnId = TurnIdForContent(itemType, advertisedKey);
		string id = advertisedKey ?? $"{itemType}:{turnId}";
		string? type = OptionalString(content, "type");
		string? text = type == "text" ? OptionalString(content, "text") : ResourceText(content);
		AcpContentState state;
		lock (_gate) {
			if (!_content.TryGetValue(id, out state!)) {
				state = new AcpContentState { Id = id, ItemType = itemType, TurnId = turnId };
				_content.Add(id, state);
			}
			state.Text.Append(text);
			state.MediaType ??= type is "image" or "audio" ? OptionalString(content, "mimeType") : null;
			state.MediaData ??= type is "image" or "audio" ? OptionalString(content, "data") : null;
			state.ResourceUri ??= OptionalString(content, "uri") ?? EmbeddedUri(content);
		}
		if (itemType == "userMessage") {
			return;
		}
		var message = new AgentPaneMessage {
			Type = deltaType,
			ProviderId = _definition.Id,
			ThreadId = SessionId(),
			TurnId = state.TurnId,
			ItemId = id,
			ItemType = itemType,
			Text = text,
			MediaType = type is "image" or "audio" ? OptionalString(content, "mimeType") : null,
			MediaData = type is "image" or "audio" ? OptionalString(content, "data") : null,
			ResourceUri = OptionalString(content, "uri") ?? EmbeddedUri(content),
		};
		PublishPane(message);
	}

	private void CompleteContentStreams() {
		AcpContentState[] content;
		lock (_gate) {
			content = [.. _content.Values];
			foreach (var state in content) _content.Remove(state.Id);
		}
		foreach (var state in content) {
			PublishPane(new AgentPaneMessage {
				Type = state.ItemType == "userMessage" ? "user-message" : "item-completed",
				ProviderId = _definition.Id,
				ThreadId = SessionId(),
				TurnId = state.TurnId,
				ItemId = state.Id,
				ItemType = state.ItemType,
				Category = state.ItemType is "thought" or "plan" ? state.ItemType : null,
				Summary = state.ItemType switch {
					"thought" => "Reasoning",
					"plan" => "Plan",
					_ => null,
				},
				Text = state.Text.Length == 0 ? null : state.Text.ToString(),
				Status = "completed",
				MediaType = state.MediaType,
				MediaData = state.MediaData,
				ResourceUri = state.ResourceUri,
			});
		}
	}

	private void UpdateTool(JsonElement update, bool initial) {
		lock (_turnTransitionGate) UpdateToolSerialized(update, initial);
	}

	private void UpdateToolSerialized(JsonElement update, bool initial) {
		string id = RequiredString(update, "toolCallId", "tool call update");
		string turnId = TurnIdForUpdate(userMessage: false);
		AcpToolState tool;
		lock (_gate) {
			bool exists = _tools.TryGetValue(id, out tool!);
			if (initial) {
				if (exists) throw new AcpProtocolException($"ACP tool call '{id}' was started more than once.");
				tool = new AcpToolState {
					Id = id,
					TurnId = turnId,
					Title = RequiredString(update, "title", "tool call"),
				};
				_tools.Add(id, tool);
			} else if (!exists) {
				throw new AcpProtocolException($"ACP updated unknown tool call '{id}'.");
			}
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
			if (tool.Status is "completed" or "failed") {
				_activeTools.Remove(id);
			} else {
				_activeTools.Add(id);
			}
		}

		bool completed = tool.Status is "completed" or "failed";
		bool replaying;
		lock (_gate) {
			replaying = _loadingTranscript;
		}
		if (!replaying && (tool.MutationMetadataDisclosed || completed)) {
			EnsureObservedMutation(tool);
		}
		if (!replaying && completed) {
			foreach (var mutation in PendingMutationCompletions(tool)) {
				Observe(new AgentToolCompleted(mutation));
			}
		}

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
		if (dispatchPending) DispatchPendingSubmission();
	}

	private string TurnIdForUpdate(bool userMessage) {
		lock (_gate) {
			if (!_loadingTranscript) {
				return _turnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
			}

			if (userMessage) _turnNumber++;
			else if (_turnNumber == 0) _turnNumber = 1;
			_replayContentRole = userMessage ? "userMessage" : "other";

			return _turnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	private string TurnIdForContent(string itemType, string? messageId) {
		lock (_gate) {
			if (!_loadingTranscript) {
				return _turnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
			}

			if (itemType == "userMessage") {
				bool newMessage = messageId is null
					? _replayContentRole != "userMessage"
					: !_content.ContainsKey(messageId);
				if (newMessage) _turnNumber++;
			} else if (_turnNumber == 0) {
				_turnNumber = 1;
			}
			_replayContentRole = itemType;
			return _turnNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}
	}

	private void PublishTool(AcpToolState tool) => PublishPane(new AgentPaneMessage {
		Type = tool.Status is "completed" or "failed" or "cancelled" or "settled"
			? "item-completed"
			: "item-started",
		ProviderId = _definition.Id,
		ThreadId = SessionId(),
		TurnId = tool.TurnId,
		ItemId = $"tool:{tool.Id}",
		ItemType = "tool",
		Category = tool.Kind,
		Summary = tool.Title,
		Text = tool.Text,
		Status = tool.Status,
		Locations = tool.Locations,
		Diffs = tool.Diffs,
		Content = tool.Content,
		TerminalId = tool.TerminalId,
		StartedAtMs = tool.StartedAtMs,
	});

	private void ReadToolContent(JsonElement content, AcpToolState tool) {
		var text = new StringBuilder();
		var diffs = new List<AgentPaneDiff>();
		var blocks = new List<AgentPaneContent>();
		tool.Text = null;
		tool.Diffs = null;
		tool.Content = null;
		tool.TerminalId = null;
		foreach (var item in content.EnumerateArray()) {
			switch (OptionalString(item, "type")) {
				case "content" when item.TryGetProperty("content", out var block):
					blocks.Add(ReadToolContentBlock(block));
					break;
				case "diff":
					string diffPath = RequiredAbsolutePath(item, "path", "tool diff");
					diffs.Add(new AgentPaneDiff {
						Path = diffPath,
						OldText = OptionalString(item, "oldText"),
						NewText = RequiredText(item, "newText", "tool diff"),
					});
					break;
				// A tool may embed a terminal the agent runs itself; only a client-created one has output here.
				case "terminal":
					tool.TerminalId = RequiredString(item, "terminalId", "tool terminal");
					if (_terminals.TryOutput(tool.TerminalId, out var terminal)) {
						AppendTerminalOutput(text, terminal);
					}
					break;
			}
		}
		tool.Text = text.Length > 0 ? text.ToString() : null;
		tool.Diffs = diffs.Count > 0 ? diffs : null;
		tool.Content = blocks.Count > 0 ? blocks : null;
	}

	private static AgentPaneContent ReadToolContentBlock(JsonElement block) {
		string type = RequiredString(block, "type", "tool content block");
		return type switch {
			"text" => new AgentPaneContent {
				Type = type,
				Text = RequiredText(block, "text", "tool text content"),
			},
			"image" or "audio" => new AgentPaneContent {
				Type = type,
				MediaType = RequiredString(block, "mimeType", $"tool {type} content"),
				MediaData = RequiredString(block, "data", $"tool {type} content"),
			},
			"resource_link" => new AgentPaneContent {
				Type = type,
				ResourceUri = RequiredString(block, "uri", "tool resource link"),
				Name = RequiredString(block, "name", "tool resource link"),
				Text = OptionalString(block, "description"),
			},
			"resource" => ReadEmbeddedToolResource(block),
			_ => throw new AcpProtocolException($"Unsupported ACP tool content block '{type}'."),
		};
	}

	private static AgentPaneContent ReadEmbeddedToolResource(JsonElement block) {
		if (!block.TryGetProperty("resource", out var resource)
			|| resource.ValueKind != JsonValueKind.Object) {
			throw new AcpProtocolException("An ACP embedded tool resource requires a resource object.");
		}
		string uri = RequiredString(resource, "uri", "embedded tool resource");
		bool hasText = resource.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String;
		bool hasBlob = resource.TryGetProperty("blob", out var blob) && blob.ValueKind == JsonValueKind.String;
		if (hasText == hasBlob) {
			throw new AcpProtocolException("An ACP embedded tool resource requires exactly one of text or blob.");
		}
		return new AgentPaneContent {
			Type = "resource",
			ResourceUri = uri,
			Text = hasText ? text.GetString() : null,
			MediaType = OptionalString(resource, "mimeType"),
			MediaData = hasBlob ? blob.GetString() : null,
		};
	}

	private static void AppendTerminalOutput(StringBuilder text, AcpTerminalOutput output) {
		if (text.Length > 0) text.AppendLine();
		if (output.Truncated) text.AppendLine("… earlier terminal output truncated …");
		text.Append(output.Output);
		if (output.ExitStatus is { } exit) {
			if (text.Length > 0 && text[^1] != '\n') text.AppendLine();
			text.Append(exit.ExitCode is { } code ? $"[exit {code}]" : $"[{exit.Signal ?? "terminated"}]");
		}
	}

	private static IReadOnlyList<AgentPaneLocation> ReadLocations(JsonElement locations) =>
		[.. locations.EnumerateArray().Select(location => new AgentPaneLocation {
			Path = RequiredAbsolutePath(location, "path", "tool location"),
			Line = ReadLocationLine(location),
		})];

	private static long? ReadLocationLine(JsonElement location) {
		if (!location.TryGetProperty("line", out var line) || line.ValueKind == JsonValueKind.Null) return null;
		if (!line.TryGetUInt32(out uint number)) {
			throw new AcpProtocolException("An ACP tool location line must be a non-negative 32-bit integer.");
		}
		return number;
	}

	private static string RequiredAbsolutePath(JsonElement value, string property, string source) {
		string path = RequiredString(value, property, source);
		return Path.IsPathFullyQualified(path)
			? path
			: throw new AcpProtocolException($"The ACP {source} requires an absolute '{property}'.");
	}

	private static AgentMutation Mutation(AcpToolState tool) {
		if (tool.Kind is not ("edit" or "delete" or "move")) {
			return new AgentMutation.None();
		}
		var paths = (tool.Locations ?? [])
			.Select(location => location.Path)
			.Concat((tool.Diffs ?? []).Select(diff => diff.Path));
		var files = paths
			.Distinct(StringComparer.Ordinal)
			.Select(path => new AgentMutation.File(path, null, ProvidesEditLocation: true))
			.Distinct()
			.ToArray();
		return files.Length switch {
			0 => new AgentMutation.None(),
			1 => files[0],
			_ => new AgentMutation.Files(files),
		};
	}

	private static string? ResourceText(JsonElement content) {
		if (!content.TryGetProperty("resource", out var resource)) {
			return OptionalString(content, "description") ?? OptionalString(content, "title");
		}
		return OptionalString(resource, "text");
	}

	private static string? EmbeddedUri(JsonElement content) =>
		content.TryGetProperty("resource", out var resource) ? OptionalString(resource, "uri") : null;

	private static bool IsInjectedContext(JsonElement content) =>
		EmbeddedUri(content) is { } uri
		&& (string.Equals(uri, "weavie://instructions", StringComparison.Ordinal)
			|| uri.EndsWith("#selection", StringComparison.Ordinal));

}
