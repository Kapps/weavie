namespace Weavie.AgentClientProtocol;

/// <summary>An immutable ACP agent launch profile.</summary>
public sealed record AcpAgentDefinition {
	/// <summary>The persistence-safe provider identifier.</summary>
	public required string Id { get; init; }

	/// <summary>The user-facing provider name.</summary>
	public required string Name { get; init; }

	/// <summary>The exact executable path or PATH command used to start the ACP agent.</summary>
	public required string Command { get; init; }

	/// <summary>The exact immutable agent arguments.</summary>
	public required IReadOnlyList<string> Arguments { get; init; }

	/// <summary>Environment entries declared by the installed launch recipe.</summary>
	public required IReadOnlyDictionary<string, string> Environment { get; init; }

	/// <summary>The registry distribution runner, or <c>custom</c> for a user-defined command.</summary>
	public required string Distribution { get; init; }
}
