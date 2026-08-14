namespace Weavie.AcpDistribution;

/// <summary>An empty ACP catalog for hosts and tests that do not expose registry management.</summary>
public sealed class EmptyAcpAgentCatalog : IAcpAgentCatalog {
	/// <summary>The shared empty catalog.</summary>
	public static EmptyAcpAgentCatalog Instance { get; } = new();

	private EmptyAcpAgentCatalog() { }

	/// <inheritdoc/>
	public event Action? Changed {
		add { }
		remove { }
	}

	/// <inheritdoc/>
	public IReadOnlyList<AcpLaunchSpec> LaunchSpecs => [];

	/// <inheritdoc/>
	public Task<IReadOnlyList<AcpRegistryAgent>> ListRegistryAsync(CancellationToken ct) =>
		Task.FromResult<IReadOnlyList<AcpRegistryAgent>>([]);

	/// <inheritdoc/>
	public Task InstallAsync(string id, string distribution, CancellationToken ct) =>
		Task.FromException(new InvalidOperationException("This host does not expose the ACP Registry."));

	/// <inheritdoc/>
	public void Remove(string id) => throw new InvalidOperationException("This host does not expose the ACP Registry.");

	/// <inheritdoc/>
	public void Reload(Action<IReadOnlyList<AcpLaunchSpec>> validate) =>
		throw new InvalidOperationException("This host does not expose ACP agents.");
}
