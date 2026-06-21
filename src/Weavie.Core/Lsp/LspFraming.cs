using System.Globalization;
using System.Text;

namespace Weavie.Core.Lsp;

/// <summary>
/// Reads and writes LSP base-protocol frames — JSON-RPC payloads prefixed by a
/// <c>Content-Length: N\r\n\r\n</c> header block — over a language server's stdio. The WebSocket side speaks
/// one JSON-RPC message per frame, so this header framing applies only on the process side.
/// </summary>
public static class LspFraming {
	private const string ContentLengthHeader = "Content-Length:";

	/// <summary>
	/// Writes one LSP frame: a <c>Content-Length</c> header (ASCII per the spec) followed by
	/// <paramref name="body"/> (UTF-8 JSON).
	/// </summary>
	/// <param name="destination">The stream to write the framed message to (a server's stdin).</param>
	/// <param name="body">The UTF-8 JSON-RPC payload to frame.</param>
	/// <param name="ct">Cancellation token.</param>
	public static async Task WriteFrameAsync(Stream destination, ReadOnlyMemory<byte> body, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(destination);

		byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
		await destination.WriteAsync(header, ct).ConfigureAwait(false);
		await destination.WriteAsync(body, ct).ConfigureAwait(false);
		await destination.FlushAsync(ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Reads one LSP frame, returning the JSON body bytes, or <see langword="null"/> at a clean end of stream
	/// (between messages). Honors only <c>Content-Length</c>; other headers are read and ignored.
	/// </summary>
	/// <param name="source">The stream to read a framed message from (a server's stdout).</param>
	/// <param name="ct">Cancellation token.</param>
	/// <returns>The JSON body bytes, or <see langword="null"/> when the stream ends between frames.</returns>
	public static async Task<byte[]?> ReadFrameAsync(Stream source, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(source);

		int contentLength = await ReadHeadersAsync(source, ct).ConfigureAwait(false);
		if (contentLength < 0) {
			return null; // clean EOF before any header byte
		}

		byte[] body = new byte[contentLength];
		int read = 0;
		while (read < contentLength) {
			int n = await source.ReadAsync(body.AsMemory(read, contentLength - read), ct).ConfigureAwait(false);
			if (n == 0) {
				return null; // truncated mid-body — treat as a closed stream
			}

			read += n;
		}

		return body;
	}

	// Reads the header block one byte at a time (headers are tiny and low-volume) until the blank
	// line, returning the parsed Content-Length, or -1 at a clean EOF before any byte was read.
	private static async Task<int> ReadHeadersAsync(Stream source, CancellationToken ct) {
		var line = new StringBuilder();
		int contentLength = -1;
		bool sawAnyByte = false;
		byte[] one = new byte[1];

		while (true) {
			int n = await source.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
			if (n == 0) {
				if (!sawAnyByte) {
					return -1;
				}

				throw new EndOfStreamException("Stream ended in the middle of an LSP header block.");
			}

			sawAnyByte = true;
			char c = (char)one[0];
			if (c == '\r') {
				continue; // CR is handled implicitly by the LF branch
			}

			if (c != '\n') {
				line.Append(c);
				continue;
			}

			if (line.Length == 0) {
				// Blank line: end of the header block.
				return contentLength >= 0
					? contentLength
					: throw new InvalidDataException("LSP frame is missing its Content-Length header.");
			}

			string headerLine = line.ToString();
			line.Clear();
			if (headerLine.StartsWith(ContentLengthHeader, StringComparison.OrdinalIgnoreCase)) {
				contentLength = int.Parse(headerLine[ContentLengthHeader.Length..].Trim(), CultureInfo.InvariantCulture);
			}
		}
	}
}
