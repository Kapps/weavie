using System.Text;
using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

public sealed partial class AcpAgentSession {
	private void PublishTool(AcpToolState tool) => Emit(ToolMessage(tool));

	private AgentPaneMessage ToolMessage(AcpToolState tool) => new() {
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
	};

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
					if (AcpContentAnnotations.IsUserVisible(block)) blocks.Add(ReadToolContentBlock(block));
					break;
				case "diff":
					string diffPath = ResolvedPath(item, "path", "tool diff");
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

	private IReadOnlyList<AgentPaneLocation> ReadLocations(JsonElement locations) =>
		[.. locations.EnumerateArray().Select(location => new AgentPaneLocation {
			Path = ResolvedPath(location, "path", "tool location"),
			Line = ReadLocationLine(location),
		})];

	private static long? ReadLocationLine(JsonElement location) {
		if (!location.TryGetProperty("line", out var line) || line.ValueKind == JsonValueKind.Null) return null;
		if (!line.TryGetUInt32(out uint number)) {
			throw new AcpProtocolException("An ACP tool location line must be a non-negative 32-bit integer.");
		}
		return number;
	}

	// A tool call names the files it touched so the pane can link to them. A relative name resolves against the
	// agent's own working directory, which is this session's workspace — refusing it would fault the connection
	// and end the session over a link.
	private string ResolvedPath(JsonElement value, string property, string source) {
		string path = RequiredString(value, property, source);
		try {
			return Path.GetFullPath(path, _context.Workspace);
		} catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) {
			throw new AcpProtocolException($"The ACP {source} has an unusable '{property}': {path}.", ex);
		}
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

}
