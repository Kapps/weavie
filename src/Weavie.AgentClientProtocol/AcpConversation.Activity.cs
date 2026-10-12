using System.Text.Json;
using Weavie.Core.Agents;

namespace Weavie.AgentClientProtocol;

internal sealed partial class AcpConversation {
	private static AgentActivity Activity(AcpToolState tool) {
		string? title = tool.Title?.Replace("`", "", StringComparison.Ordinal);
		if (tool.Subagent) return AgentActivity.Of(AgentActivityKind.Delegate, title);
		var kind = tool.Kind switch {
			"read" => AgentActivityKind.Read,
			"edit" => AgentActivityKind.Edit,
			"delete" => AgentActivityKind.Delete,
			"move" => AgentActivityKind.Move,
			"execute" => AgentActivityKind.Execute,
			_ => AgentActivityKind.Titled,
		};
		return kind switch {
			AgentActivityKind.Execute when Command(tool.Input) is { } command => AgentActivity.Of(kind, command),
			AgentActivityKind.Read or AgentActivityKind.Edit or AgentActivityKind.Delete or AgentActivityKind.Move
				when tool.Locations is [var first, ..] => AgentActivity.OfFile(kind, first.Path),
			// Without a structured subject the provider's title is the best description it gave.
			_ => AgentActivity.Of(AgentActivityKind.Titled, title),
		};
	}

	// The command a shell tool's rawInput carries, as a string or an argv array.
	private static string? Command(string? rawInput) {
		if (rawInput is null) return null;
		try {
			using var doc = JsonDocument.Parse(rawInput);
			if (doc.RootElement.ValueKind != JsonValueKind.Object
				|| !doc.RootElement.TryGetProperty("command", out var command)) return null;
			return command.ValueKind switch {
				JsonValueKind.String => command.GetString(),
				JsonValueKind.Array => string.Join(' ', command.EnumerateArray()
					.Where(part => part.ValueKind == JsonValueKind.String).Select(part => part.GetString())),
				_ => null,
			};
		} catch (JsonException) {
			return null;
		}
	}
}
