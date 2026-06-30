using System.Text;
using System.Text.Json;

namespace Weavie.Hosting;

/// <summary>
/// Builds the two host→web LSP bridge messages. <c>lsp-data</c> embeds the server's JSON-RPC frame inline (it is
/// already JSON — no base64 like the terminal); <c>lsp-exit</c> reports a channel's server ending or failing to
/// start, carrying a human reason that drives the page's reconnect/give-up toast.
/// </summary>
internal static class LspMessages {
	/// <summary>Frames a server's stdout payload as an <c>lsp-data</c> tagged with its session slot and channel.</summary>
	public static string Data(string slot, string channel, ReadOnlySpan<byte> frame) =>
		new StringBuilder(frame.Length + 80)
			.Append("{\"type\":\"lsp-data\",\"slot\":").Append(Str(slot))
			.Append(",\"channel\":").Append(Str(channel))
			.Append(",\"payload\":").Append(Encoding.UTF8.GetString(frame)).Append('}')
			.ToString();

	/// <summary>An <c>lsp-exit</c> for a channel whose server exited (<paramref name="reason"/> null) or never started.</summary>
	public static string Exit(string slot, string channel, int code, string? reason) =>
		JsonSerializer.Serialize(new { type = "lsp-exit", slot, channel, code, reason });

	private static string Str(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";
}
