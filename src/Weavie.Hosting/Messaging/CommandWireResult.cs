using System.Text.Json;
using Weavie.Core.Commands;

namespace Weavie.Hosting.Messaging;

/// <summary>A <see cref="CommandResult"/> as the web receives it, with its data as parsed JSON.</summary>
internal sealed record CommandWireResult(bool Ok, string? Message, string? Error, JsonElement? Data) {
	public static CommandWireResult From(CommandResult result) {
		JsonElement? data = null;
		if (!string.IsNullOrWhiteSpace(result.DataJson)) {
			using var document = JsonDocument.Parse(result.DataJson);
			data = document.RootElement.Clone();
		}

		return new CommandWireResult(result.Ok, result.Message, result.Error, data);
	}
}
