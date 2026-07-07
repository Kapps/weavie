using System.Text.Json;
using Weavie.Hosting.Agents.Codex;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class CodexHookTrustGateTests {
	[Fact]
	public void ThrowIfUnsafe_AllowsWeavieSessionFlagHooksAndTrustedHooks() {
		using var doc = JsonDocument.Parse(
			"""{"data":[{"cwd":"/repo","errors":[],"warnings":[],"hooks":[{"enabled":true,"isManaged":false,"source":"sessionFlags","trustStatus":"untrusted"},{"enabled":true,"isManaged":false,"source":"user","trustStatus":"trusted"},{"enabled":true,"isManaged":false,"source":"system","trustStatus":"managed"}]}]}""");

		CodexHookTrustGate.ThrowIfUnsafe(doc.RootElement);
	}

	[Fact]
	public void ThrowIfUnsafe_BlocksUntrustedNonWeavieHooks() {
		using var doc = JsonDocument.Parse(
			"""{"data":[{"cwd":"/repo","errors":[],"warnings":[],"hooks":[{"enabled":true,"isManaged":false,"source":"user","trustStatus":"modified","command":"evil"}]}]}""");

		var ex = Assert.Throws<InvalidOperationException>(() => CodexHookTrustGate.ThrowIfUnsafe(doc.RootElement));

		Assert.Contains("hook-trust bypass", ex.Message, StringComparison.Ordinal);
		Assert.Contains("evil", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ThrowIfUnsafe_BlocksHookMetadataErrors() {
		using var doc = JsonDocument.Parse(
			"""{"data":[{"cwd":"/repo","errors":[{"message":"bad hook","path":"/repo/.codex/hooks.json"}],"warnings":[],"hooks":[]}]}""");

		var ex = Assert.Throws<InvalidOperationException>(() => CodexHookTrustGate.ThrowIfUnsafe(doc.RootElement));

		Assert.Contains("hook metadata errors", ex.Message, StringComparison.Ordinal);
		Assert.Contains("/repo/.codex/hooks.json", ex.Message, StringComparison.Ordinal);
	}
}
