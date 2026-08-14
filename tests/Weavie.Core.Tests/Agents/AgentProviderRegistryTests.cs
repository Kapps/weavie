using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Core.Tests.Agents;

/// <summary>Provider registration and lookup are explicit.</summary>
public sealed class AgentProviderRegistryTests {
	[Fact]
	public void RequireAvailable_WithRegisteredProvider_ReturnsIt() {
		var registry = new AgentProviderRegistry();
		var provider = new FakeProvider("claude");
		registry.Register(provider);

		Assert.Same(provider, registry.RequireAvailable("claude"));
	}

	[Fact]
	public void RequireAvailable_WithUnknownProvider_FailsLoudly() {
		var registry = new AgentProviderRegistry();
		Assert.Throws<InvalidOperationException>(() => registry.RequireAvailable("unknown"));
	}

	[Fact]
	public void DuplicateProviderId_IsRejected() {
		var registry = new AgentProviderRegistry();
		registry.Register(new FakeProvider("claude"));
		Assert.Throws<InvalidOperationException>(() => registry.Register(new FakeProvider("claude")));
	}

	[Fact]
	public void RequireAvailable_WithUnavailableProvider_FailsLoudly() {
		var registry = new AgentProviderRegistry();
		registry.Register(new FakeProvider("structured", available: false));

		var ex = Assert.Throws<InvalidOperationException>(() => registry.RequireAvailable("structured"));
		Assert.Contains("not exposed", ex.Message);
	}

	[Fact]
	public void ReplaceAllPublishesOneAtomicCatalogChange() {
		var registry = new AgentProviderRegistry();
		int changes = 0;
		registry.Changed += () => changes++;

		registry.ReplaceAll([new FakeProvider("claude"), new FakeProvider("acp")]);

		Assert.Equal(1, changes);
		Assert.Equal(["claude", "acp"], registry.Providers.Select(provider => provider.Info.Id));
	}

	[Fact]
	public void InvalidReplacementLeavesTheCurrentCatalogUntouched() {
		var registry = new AgentProviderRegistry();
		registry.Register(new FakeProvider("claude"));

		Assert.Throws<InvalidOperationException>(() => registry.ReplaceAll([
			new FakeProvider("acp"),
			new FakeProvider("acp"),
		]));

		Assert.Equal("claude", Assert.Single(registry.Providers).Info.Id);
	}

	private sealed class FakeProvider : IAgentProvider {
		public FakeProvider(string id) : this(id, available: true) { }

		public FakeProvider(string id, bool available) {
			Info = new AgentProviderInfo {
				Id = id,
				Name = id,
				Capabilities = AgentProviderCapabilities.Terminal,
				Available = available,
				UnavailableReason = available ? null : $"{id} is not exposed.",
			};
		}

		public AgentProviderInfo Info { get; }

		public IAgentSession CreateSession(AgentSessionContext context) => throw new NotSupportedException();
	}
}
