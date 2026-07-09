using Xunit;

namespace Weavie.Hosting.Tests;

internal static class TestCollections {
	public const string HostIntegration = "host-integration";
}

[CollectionDefinition(TestCollections.HostIntegration, DisableParallelization = true)]
public sealed class HostIntegrationCollection {
}
