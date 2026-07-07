using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests.Mcp;

/// <summary>
/// The host-identity block included in embedded-agent guidance: a managed worker reports the build it actually loaded.
/// </summary>
public sealed class HostRuntimeInfoTests {
	[Fact]
	public void Resolve_ManagedWorker_ReportsItsLoadedBuild() {
		string workerDir = Path.Combine(Path.GetTempPath(), "wv", "versions", "30", "worker");
		var info = HostRuntimeInfo.Resolve(HostTransport.Remote, workerDir, devVersion: "0.1.247");
		Assert.Equal(HostTransport.Remote, info.Transport);
		Assert.True(info.Managed);
		Assert.Equal("30", info.Build);
	}

	[Fact]
	public void Resolve_UnmanagedHost_ReportsTheDevVersion() {
		var info = HostRuntimeInfo.Resolve(HostTransport.Local, Path.GetTempPath(), devVersion: "0.1.247");
		Assert.False(info.Managed);
		Assert.Equal("0.1.247", info.Build);
	}

	[Fact]
	public void Compose_RemoteManaged_StartsWithAppendixAndDescribesTheWorker() {
		string text = EmbeddedAgentGuidance.Compose(new HostRuntimeInfo(HostTransport.Remote, Managed: true, "114"));
		Assert.StartsWith(EmbeddedAgentGuidance.Instructions, text, StringComparison.Ordinal);
		Assert.Contains("## Host runtime", text, StringComparison.Ordinal);
		Assert.Contains("remote (network-exposed worker)", text, StringComparison.Ordinal);
		Assert.Contains("114 (runner-managed worker)", text, StringComparison.Ordinal);
	}

	[Fact]
	public void Compose_LocalDev_DescribesALocalDevBuild() {
		string text = EmbeddedAgentGuidance.Compose(new HostRuntimeInfo(HostTransport.Local, Managed: false, "0.1.247"));
		Assert.Contains("local (loopback only)", text, StringComparison.Ordinal);
		Assert.Contains("0.1.247 (local dev build)", text, StringComparison.Ordinal);
	}
}
