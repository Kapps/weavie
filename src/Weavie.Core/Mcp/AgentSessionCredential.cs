using System.Security.Cryptography;

namespace Weavie.Core.Mcp;

/// <summary>The per-session credential shared by the capability registry and provider-specific IDE integration.</summary>
public sealed record AgentSessionCredential {
	/// <summary>Creates a fresh 128-bit lowercase-hex bearer token.</summary>
	public static AgentSessionCredential Create() => new() {
		Token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
	};

	/// <summary>The bearer token.</summary>
	public required string Token { get; init; }
}
