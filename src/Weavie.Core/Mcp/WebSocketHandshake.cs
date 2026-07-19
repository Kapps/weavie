using System.Net.Sockets;
using System.Text;

namespace Weavie.Core.Mcp;

/// <summary>
/// Minimal HTTP/1.1 + WebSocket-upgrade plumbing shared by the two loopback servers (IDE-MCP + LSP bridge):
/// read the request line + headers, write a bare status, complete the 101 upgrade. They bind a raw
/// <see cref="TcpListener"/> (not <c>HttpListener</c>, which needs URL ACLs on Windows), so hand-roll this.
/// </summary>
internal static class WebSocketHandshake {
	private const int MaxHeaderBytes = 64 * 1024;

	/// <summary>
	/// Reads the request line + headers up to the blank-line terminator, or null if the peer closed or flooded
	/// the header buffer.
	/// </summary>
	public static async Task<HttpRequestHead?> ReadRequestAsync(
		NetworkStream stream, CancellationToken ct) {
		var sb = new StringBuilder();
		byte[] one = new byte[1];
		int matched = 0; // counts the "\r\n\r\n" terminator
		while (matched < 4) {
			int n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
			if (n == 0) {
				return null;
			}

			char c = (char)one[0];
			sb.Append(c);
			matched = c switch {
				'\r' when matched is 0 or 2 => matched + 1,
				'\n' when matched is 1 or 3 => matched + 1,
				_ => 0,
			};

			if (sb.Length > MaxHeaderBytes) {
				return null; // header flood guard
			}
		}

		string[] lines = sb.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
		if (lines.Length == 0) {
			return null;
		}

		string[] requestParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
		string method = requestParts.Length >= 1 ? requestParts[0] : "GET";
		string target = requestParts.Length >= 2 ? requestParts[1] : "/";

		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string line in lines.Skip(1)) {
			int colon = line.IndexOf(':', StringComparison.Ordinal);
			if (colon > 0) {
				headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
			}
		}

		return new HttpRequestHead(method, target, headers);
	}

	/// <summary>Reads the request body declared by <c>Content-Length</c>.</summary>
	public static async Task<string> ReadBodyAsync(
		NetworkStream stream, IReadOnlyDictionary<string, string> headers, CancellationToken ct) {
		if (!headers.TryGetValue("content-length", out string? value) || !int.TryParse(value, out int length) || length < 0) {
			return string.Empty;
		}

		byte[] bytes = new byte[length];
		int offset = 0;
		while (offset < bytes.Length) {
			int read = await stream.ReadAsync(bytes.AsMemory(offset, bytes.Length - offset), ct).ConfigureAwait(false);
			if (read == 0) {
				throw new IOException("HTTP request body ended early.");
			}

			offset += read;
		}

		return Encoding.UTF8.GetString(bytes);
	}

	/// <summary>Writes a bare HTTP status response (no body) and flushes.</summary>
	public static async Task WriteStatusAsync(NetworkStream stream, string status, CancellationToken ct) {
		string response = $"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
		await stream.FlushAsync(ct).ConfigureAwait(false);
	}

	/// <summary>Writes a JSON HTTP response and flushes.</summary>
	public static async Task WriteJsonAsync(NetworkStream stream, string status, string json, CancellationToken ct) {
		byte[] body = Encoding.UTF8.GetBytes(json);
		string response =
			$"HTTP/1.1 {status}\r\n" +
			"Connection: close\r\n" +
			"Content-Type: application/json\r\n" +
			$"Content-Length: {body.Length}\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
		await stream.WriteAsync(body, ct).ConfigureAwait(false);
		await stream.FlushAsync(ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Returns <paramref name="supported"/> when the client's <c>Sec-WebSocket-Protocol</c> offer includes it,
	/// else null. WHATWG clients (Claude Code's Bun WebSocket sends <c>protocols: ["mcp"]</c>) fail the whole
	/// connection unless the 101 selects one of their offered subprotocols, so the server must echo it.
	/// </summary>
	public static string? SelectSubProtocol(IReadOnlyDictionary<string, string> headers, string supported) {
		if (!headers.TryGetValue("sec-websocket-protocol", out string? offered)) {
			return null;
		}

		foreach (string candidate in offered.Split(',')) {
			if (string.Equals(candidate.Trim(), supported, StringComparison.Ordinal)) {
				return supported;
			}
		}

		return null;
	}

	/// <summary>
	/// Completes the upgrade for <paramref name="webSocketKey"/> (writes 101 Switching Protocols + accept),
	/// selecting <paramref name="subProtocol"/> when non-null.
	/// </summary>
	public static async Task WriteUpgradeAsync(
		NetworkStream stream, string webSocketKey, string? subProtocol, CancellationToken ct) {
		string accept = IdeLockFile.ComputeWebSocketAccept(webSocketKey);
		string protocolLine = subProtocol is null ? string.Empty : $"Sec-WebSocket-Protocol: {subProtocol}\r\n";
		string response =
			"HTTP/1.1 101 Switching Protocols\r\n" +
			"Upgrade: websocket\r\n" +
			"Connection: Upgrade\r\n" +
			$"Sec-WebSocket-Accept: {accept}\r\n" +
			protocolLine + "\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
		await stream.FlushAsync(ct).ConfigureAwait(false);
	}
}

internal sealed record HttpRequestHead(
	string Method,
	string Target,
	Dictionary<string, string> Headers);
