using Weavie.AgentClientProtocol;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpProcessInvocationTests {
	[Theory]
	[InlineData("agent-package@1.2.3", "agent-package@<=1.2.3")]
	[InlineData("@scope/agent-package@1.2.3-beta.1+build.2", "@scope/agent-package@<=1.2.3-beta.1+build.2")]
	[InlineData("@scope/agent-package", "@scope/agent-package")]
	[InlineData("agent-package@latest", "agent-package@latest")]
	[InlineData("agent-package@^1.2.3", "agent-package@^1.2.3")]
	[InlineData("agent-package@<=1.2.3", "agent-package@<=1.2.3")]
	[InlineData("agent-package@1.2", "agent-package@1.2")]
	[InlineData("agent-package@01.2.3", "agent-package@01.2.3")]
	[InlineData("agent-package@1.2.3-01", "agent-package@1.2.3-01")]
	public void ExactNpmVersionsBecomeRegistryCeilings(string packageSpec, string expected) =>
		Assert.Equal(expected, AcpProcessInvocation.BoundNpmPackageSpec(packageSpec));

	[Fact]
	public void PersistedExactNpxRecipeResolvesToABoundedInvocation() {
		var definition = new AcpAgentDefinition {
			Id = "sample",
			Name = "Sample",
			Command = "npx",
			Arguments = ["--yes", "@scope/agent@1.2.3", "--stdio"],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal),
			Distribution = "npx",
		};

		var invocation = AcpProcessInvocation.Resolve(
			definition,
			Directory.GetCurrentDirectory(),
			[],
			windows: false,
			pathValue: string.Empty);

		Assert.Equal("npx", invocation.Command);
		Assert.Equal(["--yes", "@scope/agent@<=1.2.3", "--stdio"], invocation.Arguments);
	}

	[Fact]
	public void WindowsNpxEscapesTheGeneratedComparatorFromCmdRedirection() {
		string systemDirectory = Path.Combine(Path.GetTempPath(), "Windows", "System32");
		var invocation = AcpProcessInvocation.WrapWindowsNpx(
			@"C:\Program Files\node\npx.cmd",
			["--yes", "@scope/agent@<=1.2.3", "--stdio"],
			systemDirectory);

		Assert.Equal(Path.Combine(systemDirectory, "cmd.exe"), invocation.Command);
		Assert.Equal(
			["/d", "/s", "/v:off", "/c", "\"C:\\Program Files\\node\\npx.cmd\" --yes @scope/agent@^<=1.2.3 --stdio"],
			invocation.Arguments);
	}
}
