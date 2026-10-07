using System.Text.Json;

namespace Weavie.Hosting;

/// <summary>
/// Builds host→web LSP payloads. Data embeds the server's JSON-RPC frame inline; exit reports a channel's server
/// ending or failing to start, carrying a human reason that drives the page's reconnect/give-up toast.
/// </summary>
internal static class LspMessages {
	/// <summary>Frames a server's stdout payload with its channel.</summary>
	public static LspDataWire Data(string channel, ReadOnlySpan<byte> frame) {
		var reader = new Utf8JsonReader(frame);
		return new(channel, JsonElement.ParseValue(ref reader));
	}

	/// <summary>An exit for a channel whose server ended (<paramref name="reason"/> null) or never started.</summary>
	public static LspExitWire Exit(string channel, int code, string? reason) => new(channel, code, reason);
}

internal sealed record LspDataWire(string Channel, JsonElement Payload);

internal sealed record LspExitWire(string Channel, int Code, string? Reason);
