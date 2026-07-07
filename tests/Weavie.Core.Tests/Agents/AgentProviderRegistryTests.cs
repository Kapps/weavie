using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Core.Tests.Agents;

/// <summary>Provider registration is explicit and the compatibility phase requires exactly one provider.</summary>
public sealed class AgentProviderRegistryTests {
	[Fact]
	public void Sole_WithOneProvider_ReturnsIt() {
		var registry = new AgentProviderRegistry();
		var provider = new FakeProvider("claude");
		registry.Register(provider);

		Assert.Same(provider, registry.Sole());
	}

	[Fact]
	public void Sole_WithNoProvider_FailsLoudly() {
		var registry = new AgentProviderRegistry();
		Assert.Throws<InvalidOperationException>(registry.Sole);
	}

	[Fact]
	public void DuplicateProviderId_IsRejected() {
		var registry = new AgentProviderRegistry();
		registry.Register(new FakeProvider("claude"));
		Assert.Throws<InvalidOperationException>(() => registry.Register(new FakeProvider("claude")));
	}

	private sealed class FakeProvider(string id) : IAgentProvider {
		public AgentProviderInfo Info { get; } = new() {
			Id = id,
			Name = id,
			Capabilities = AgentProviderCapabilities.Terminal,
		};

		public IAgentSession CreateSession(AgentSessionContext context) => throw new NotSupportedException();
	}
}
