using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Weavie.Hosting.Desktop;

/// <summary>What the running instance did with the paths a second launch handed it.</summary>
/// <param name="Accepted">Whether the running instance opened them.</param>
/// <param name="Root">The workspace the caller should boot into when it was declined; empty when accepted.</param>
public readonly record struct HandoffReply(bool Accepted, string Root);

/// <summary>
/// Wire protocol between a second launch and the already-running app: a named pipe carrying one
/// length-prefixed request and reply, framed by <see cref="Weavie.Core.Hooks.HookProtocol"/>. No token — the OS
/// pipe ACL (current user only) is the auth, and the name is derived from <see cref="Weavie.Core.WeaviePaths.Root"/>
/// so two roots (and so two parallel test runs) never share an instance.
/// </summary>
public static class InstanceProtocol {
	/// <summary>How long a second launch waits to reach a running instance before booting its own.</summary>
	public const int ConnectTimeoutMs = 500;

	/// <summary>The per-root pipe name. Short by construction: macOS caps a Unix socket path at 104 bytes.</summary>
	public static string PipeName(string weavieRoot) {
		ArgumentException.ThrowIfNullOrEmpty(weavieRoot);
		byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(weavieRoot)));
		return $"weavie-open-{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
	}

	/// <summary>Serializes the paths a second launch is handing over.</summary>
	public static byte[] EncodeRequest(IReadOnlyList<string> paths) {
		ArgumentNullException.ThrowIfNull(paths);
		return JsonSerializer.SerializeToUtf8Bytes(new HandoffRequest(paths));
	}

	/// <summary>Reads a handover request, or null when the frame was not one.</summary>
	public static IReadOnlyList<string>? DecodeRequest(byte[] payload) {
		ArgumentNullException.ThrowIfNull(payload);
		try {
			return JsonSerializer.Deserialize<HandoffRequest>(payload)?.Paths;
		} catch (JsonException) {
			return null;
		}
	}

	/// <summary>Serializes the running instance's answer.</summary>
	public static byte[] EncodeReply(HandoffReply reply) => JsonSerializer.SerializeToUtf8Bytes(reply);

	/// <summary>Reads an answer, treating an unreadable one as "not handled".</summary>
	public static HandoffReply DecodeReply(byte[] payload) {
		ArgumentNullException.ThrowIfNull(payload);
		try {
			return JsonSerializer.Deserialize<HandoffReply>(payload);
		} catch (JsonException) {
			return new HandoffReply(false, string.Empty);
		}
	}

	private sealed record HandoffRequest(IReadOnlyList<string> Paths);
}
