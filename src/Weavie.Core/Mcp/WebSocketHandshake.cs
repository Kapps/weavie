using System.Net.Sockets;
using System.Text;

namespace Weavie.Core.Mcp;

/// <summary>
/// Minimal HTTP/1.1 + WebSocket-upgrade plumbing shared by the two loopback servers (the IDE-MCP server and
/// the LSP bridge): read the request line + headers, write a bare status response, and complete the 101
/// upgrade. Both bind a raw <see cref="TcpListener"/> rather than <c>HttpListener</c> (which would need URL
/// ACLs on Windows), so they hand-roll this much of the protocol.
/// </summary>
internal static class WebSocketHandshake {
	private const int MaxHeaderBytes = 64 * 1024;

	/// <summary>
	/// Reads the request line + headers up to the blank-line terminator. Returns the request target (path +
	/// query) and a case-insensitive header map, or null if the peer closed or flooded the header buffer.
	/// </summary>
	public static async Task<(string Target, Dictionary<string, string> Headers)?> ReadRequestAsync(
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
		string target = requestParts.Length >= 2 ? requestParts[1] : "/";

		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string line in lines.Skip(1)) {
			int colon = line.IndexOf(':', StringComparison.Ordinal);
			if (colon > 0) {
				headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
			}
		}

		return (target, headers);
	}

	/// <summary>Writes a bare HTTP status response (no body) and flushes.</summary>
	public static async Task WriteStatusAsync(NetworkStream stream, string status, CancellationToken ct) {
		string response = $"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
		await stream.FlushAsync(ct).ConfigureAwait(false);
	}

	/// <summary>Completes the upgrade for <paramref name="webSocketKey"/> (writes 101 Switching Protocols + accept).</summary>
	public static async Task WriteUpgradeAsync(NetworkStream stream, string webSocketKey, CancellationToken ct) {
		string accept = IdeLockFile.ComputeWebSocketAccept(webSocketKey);
		string response =
			"HTTP/1.1 101 Switching Protocols\r\n" +
			"Upgrade: websocket\r\n" +
			"Connection: Upgrade\r\n" +
			$"Sec-WebSocket-Accept: {accept}\r\n\r\n";
		await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
		await stream.FlushAsync(ct).ConfigureAwait(false);
	}
}
