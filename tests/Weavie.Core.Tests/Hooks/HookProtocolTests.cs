using System.Text;
using Weavie.Core.Hooks;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>The length-prefixed pipe framing: payloads round-trip; truncated input returns null.</summary>
public sealed class HookProtocolTests {
	[Fact]
	public async Task WriteThenRead_RoundTripsPayload() {
		using var stream = new MemoryStream();
		byte[] payload = Encoding.UTF8.GetBytes("hello hook");

		await HookProtocol.WriteFramedAsync(stream, payload, CancellationToken.None);
		stream.Position = 0;
		byte[]? read = await HookProtocol.ReadFramedAsync(stream, CancellationToken.None);

		Assert.Equal(payload, read);
	}

	[Fact]
	public async Task ReadFramed_EmptyFrame_ReturnsEmpty() {
		using var stream = new MemoryStream();

		await HookProtocol.WriteFramedAsync(stream, [], CancellationToken.None);
		stream.Position = 0;
		byte[]? read = await HookProtocol.ReadFramedAsync(stream, CancellationToken.None);

		Assert.NotNull(read);
		Assert.Empty(read!);
	}

	[Fact]
	public async Task ReadFramed_TruncatedHeader_ReturnsNull() {
		using var stream = new MemoryStream([1, 2]);

		Assert.Null(await HookProtocol.ReadFramedAsync(stream, CancellationToken.None));
	}
}
