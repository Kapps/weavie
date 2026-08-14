namespace Weavie.AcpDistribution;

/// <summary>An immutable installed ACP agent launch recipe.</summary>
public sealed record AcpLaunchSpec {
	/// <summary>The registry or custom provider identifier.</summary>
	public required string Id { get; init; }

	/// <summary>The user-facing agent name.</summary>
	public required string Name { get; init; }

	/// <summary>The installed registry version, or <c>null</c> for a custom command.</summary>
	public string? Version { get; init; }

	/// <summary>The exact executable or PATH command.</summary>
	public required string Command { get; init; }

	/// <summary>The exact immutable arguments.</summary>
	public required IReadOnlyList<string> Arguments { get; init; }

	/// <summary>Environment entries declared by the launch recipe.</summary>
	public required IReadOnlyDictionary<string, string> Environment { get; init; }

	/// <summary>The selected registry distribution kind, or <c>custom</c>.</summary>
	public required string Distribution { get; init; }
}

/// <summary>The installed ACP catalog and the official registry operations.</summary>
public interface IAcpAgentCatalog {
	/// <summary>Raised after the installed provider set changes.</summary>
	event Action? Changed;

	/// <summary>Installed registry agents and user-defined commands.</summary>
	IReadOnlyList<AcpLaunchSpec> LaunchSpecs { get; }

	/// <summary>Reads the current official registry and joins it with local install state.</summary>
	Task<IReadOnlyList<AcpRegistryAgent>> ListRegistryAsync(CancellationToken ct);

	/// <summary>Installs or updates one exact registry distribution.</summary>
	Task InstallAsync(string id, string distribution, CancellationToken ct);

	/// <summary>Removes one installed registry agent.</summary>
	void Remove(string id);

	/// <summary>Reloads installed and custom launch recipes after <paramref name="validate"/> accepts them.</summary>
	void Reload(Action<IReadOnlyList<AcpLaunchSpec>> validate);
}

/// <summary>One agent available from the official ACP Registry.</summary>
public sealed record AcpRegistryAgent {
	/// <summary>The registry identifier.</summary>
	public required string Id { get; init; }

	/// <summary>The registry display name.</summary>
	public required string Name { get; init; }

	/// <summary>The current registry version.</summary>
	public required string Version { get; init; }

	/// <summary>The registry description.</summary>
	public required string Description { get; init; }

	/// <summary>Distribution kinds available on this machine.</summary>
	public required IReadOnlyList<string> Distributions { get; init; }

	/// <summary>The installed kind, when installed.</summary>
	public string? InstalledDistribution { get; init; }

	/// <summary>The installed version, when installed.</summary>
	public string? InstalledVersion { get; init; }
}
