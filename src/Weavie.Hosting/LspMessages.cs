using System.Text.Json;

namespace Weavie.Hosting;

/// <summary>
/// Builds host→web LSP payloads. Data embeds the server's JSON-RPC frame inline; exit reports a channel's server
/// ending or failing to start, carrying a human reason that drives the page's reconnect/give-up toast.
/// </summary>
internal static class LspMessages {
	/// <summary>Frames a server's stdout payload with its channel.</summary>
	public static object Data(string channel, ReadOnlySpan<byte> frame) =>
		new { channel, payload = JsonSerializer.Deserialize<JsonElement>(frame) };

	/// <summary>An exit for a channel whose server ended (<paramref name="reason"/> null) or never started.</summary>
	public static object Exit(string channel, int code, string? reason) =>
		new { channel, code, reason };
}
