using System.Text;
using ZstdSharp;

namespace Weavie.Headless.Tests;

internal static class TestWebSocketCodec {
	public static byte[] Encode(string json) {
		using var compressor = new Compressor(3);
		return compressor.Wrap(Encoding.UTF8.GetBytes(json)).ToArray();
	}

	public static byte[] Decode(ReadOnlySpan<byte> bytes) {
		using var decompressor = new Decompressor();
		return decompressor.Unwrap(bytes).ToArray();
	}
}
