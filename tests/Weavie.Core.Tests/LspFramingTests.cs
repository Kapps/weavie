using System.Text;
using Weavie.Core.Lsp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Verifies the LSP base-protocol framing (the <c>Content-Length</c> header layer the bridge applies
/// on the server's stdio) round-trips and tolerates the header variations real servers emit.
/// </summary>
public sealed class LspFramingTests {
	private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

	[Fact]
	public async Task WriteFrame_EmitsContentLengthHeaderAndBody() {
		using var stream = new MemoryStream();
		var body = Utf8("{\"jsonrpc\":\"2.0\"}");
		await LspFraming.WriteFrameAsync(stream, body, CancellationToken.None);

		var text = Encoding.UTF8.GetString(stream.ToArray());
		Assert.Equal($"Content-Length: {body.Length}\r\n\r\n{{\"jsonrpc\":\"2.0\"}}", text);
	}

	[Fact]
	public async Task WriteThenRead_RoundTripsBody() {
		using var stream = new MemoryStream();
		var body = Utf8("{\"method\":\"initialize\",\"id\":1}");
		await LspFraming.WriteFrameAsync(stream, body, CancellationToken.None);
		stream.Position = 0;

		var read = await LspFraming.ReadFrameAsync(stream, CancellationToken.None);
		Assert.NotNull(read);
		Assert.Equal(body, read);
	}

	[Fact]
	public async Task ReadFrame_ReadsMultipleFramesInSequence() {
		using var stream = new MemoryStream();
		await LspFraming.WriteFrameAsync(stream, Utf8("first"), CancellationToken.None);
		await LspFraming.WriteFrameAsync(stream, Utf8("second"), CancellationToken.None);
		stream.Position = 0;

		Assert.Equal("first", Encoding.UTF8.GetString((await LspFraming.ReadFrameAsync(stream, CancellationToken.None))!));
		Assert.Equal("second", Encoding.UTF8.GetString((await LspFraming.ReadFrameAsync(stream, CancellationToken.None))!));
		Assert.Null(await LspFraming.ReadFrameAsync(stream, CancellationToken.None));
	}

	[Fact]
	public async Task ReadFrame_IgnoresExtraHeadersLikeContentType() {
		using var stream = new MemoryStream();
		var raw = "Content-Length: 4\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\nping";
		stream.Write(Encoding.ASCII.GetBytes(raw));
		stream.Position = 0;

		var read = await LspFraming.ReadFrameAsync(stream, CancellationToken.None);
		Assert.Equal("ping", Encoding.UTF8.GetString(read!));
	}

	[Fact]
	public async Task ReadFrame_ReturnsNull_OnCleanEof() {
		using var stream = new MemoryStream();
		Assert.Null(await LspFraming.ReadFrameAsync(stream, CancellationToken.None));
	}

	[Fact]
	public async Task ReadFrame_HandlesBinaryBodyBytesByLength() {
		using var stream = new MemoryStream();
		// A body whose bytes include characters that must be counted by Content-Length, not by lines.
		var body = Utf8("{\"text\":\"line1\\nline2\\r\\nend\"}");
		await LspFraming.WriteFrameAsync(stream, body, CancellationToken.None);
		stream.Position = 0;

		var read = await LspFraming.ReadFrameAsync(stream, CancellationToken.None);
		Assert.Equal(body, read);
	}
}
