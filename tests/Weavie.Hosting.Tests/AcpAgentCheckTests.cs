using Weavie.AgentClientProtocol;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpAgentCheckTests {
	[Fact]
	public async Task AnAgentThatAnswersInitializePasses() =>
		await AcpAgentCheck.VerifyAsync(
			Definition(
				AcpAgentSessionFixture.ExecutablePath("tools", "Weavie.FakeAcp", "weavie-fake-acp"),
				["inference", "ok"]),
			CancellationToken.None);

	[Fact]
	public async Task AProgramThatExitsWithoutAnsweringFailsWithItsErrorOutput() {
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AcpAgentCheck.VerifyAsync(
			Definition("dotnet", ["no-such-weavie-command"]),
			CancellationToken.None));

		Assert.StartsWith("Checked Agent started but didn't answer as an ACP agent", error.Message, StringComparison.Ordinal);
		Assert.Contains("isn't an ACP message", error.Message, StringComparison.Ordinal);
		Assert.Contains("Could not execute because the specified command or file was not found.", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AMissingCommandFailsToStart() {
		var error = await Assert.ThrowsAsync<InvalidOperationException>(() => AcpAgentCheck.VerifyAsync(
			Definition("definitely-not-a-weavie-agent", []),
			CancellationToken.None));

		Assert.StartsWith("Checked Agent could not be started", error.Message, StringComparison.Ordinal);
	}

	private static AcpAgentDefinition Definition(string command, IReadOnlyList<string> arguments) => new() {
		Id = "checked",
		Name = "Checked Agent",
		Command = command,
		Arguments = arguments,
		Environment = new Dictionary<string, string>(StringComparer.Ordinal),
		Distribution = "npx",
	};
}
