using System.Buffers.Binary;
using System.Globalization;

namespace Weavie.Core.Hooks;

/// <summary>
/// The tiny wire protocol between the hook relay (Claude's <c>command</c> hook) and the in-process
/// <see cref="HookBridgeServer"/>: a current-user-only named pipe carrying one length-prefixed request and
/// one length-prefixed response. No bearer token — the OS pipe ACL (current user only) is the auth, so a web
/// page or another OS user cannot reach it (closing the loopback-token exposure the MCP servers carry).
/// </summary>
public static class HookProtocol {
	/// <summary>Env var (injected into the spawned claude, inherited by its hook child) naming the pipe to dial.</summary>
	public const string PipeEnvVar = "WEAVIE_HOOK_PIPE";

	private const int MaxFrameBytes = 16 * 1024 * 1024;

	/// <summary>The per-instance pipe name, scoped to the IDE server's ephemeral <paramref name="port"/>.</summary>
	/// <param name="port">The IDE server port, reused as a unique per-instance suffix.</param>
	public static string PipeName(int port) => $"weavie-hook-{port.ToString(CultureInfo.InvariantCulture)}";

	/// <summary>Writes a length-prefixed (4-byte little-endian) frame, then flushes.</summary>
	/// <param name="stream">The pipe stream.</param>
	/// <param name="payload">The frame body (may be empty).</param>
	/// <param name="ct">Cancellation token.</param>
	public static async Task WriteFramedAsync(Stream stream, byte[] payload, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(stream);
		ArgumentNullException.ThrowIfNull(payload);

		byte[] header = new byte[4];
		BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
		await stream.WriteAsync(header, ct).ConfigureAwait(false);
		if (payload.Length > 0) {
			await stream.WriteAsync(payload, ct).ConfigureAwait(false);
		}
		await stream.FlushAsync(ct).ConfigureAwait(false);
	}

	/// <summary>
	/// Reads one length-prefixed frame, or <see langword="null"/> if the peer closed before a full frame
	/// arrived (or the declared length was out of range).
	/// </summary>
	/// <param name="stream">The pipe stream.</param>
	/// <param name="ct">Cancellation token.</param>
	public static async Task<byte[]?> ReadFramedAsync(Stream stream, CancellationToken ct) {
		ArgumentNullException.ThrowIfNull(stream);

		byte[] header = new byte[4];
		if (!await ReadExactlyAsync(stream, header, ct).ConfigureAwait(false)) {
			return null;
		}

		int length = BinaryPrimitives.ReadInt32LittleEndian(header);
		if (length is < 0 or > MaxFrameBytes) {
			return null;
		}
		if (length == 0) {
			return [];
		}

		byte[] body = new byte[length];
		return await ReadExactlyAsync(stream, body, ct).ConfigureAwait(false) ? body : null;
	}

	private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct) {
		int offset = 0;
		while (offset < buffer.Length) {
			int read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
			if (read == 0) {
				return false;
			}
			offset += read;
		}
		return true;
	}
}
