using Weavie.AgentClientProtocol;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AcpProcessInvocationTests {
	[Theory]
	[InlineData("agent-package@1.2.3")]
	[InlineData("@scope/agent@1.2.3-beta.1+build.2")]
	[InlineData("@scope/agent@latest")]
	[InlineData("@scope/agent@^1.2.3")]
	public void NpxLaunchPreservesRegistryPackageAndDisablesReleaseAge(string packageSpec) {
		var definition = new AcpAgentDefinition {
			Id = "sample",
			Name = "Sample",
			Command = "npx",
			Arguments = ["--yes", packageSpec, "--stdio"],
			Environment = new Dictionary<string, string>(StringComparer.Ordinal),
			Distribution = "npx",
		};

		var invocation = AcpProcessInvocation.Resolve(
			definition,
			Directory.GetCurrentDirectory(),
			["--login"],
			windows: false,
			pathValue: string.Empty);

		Assert.Equal("npx", invocation.Command);
		Assert.Equal(
			[
				"--yes",
				"--no-audit",
				"--no-fund",
				"--no-update-notifier",
				"--min-release-age=0",
				"--",
				packageSpec,
				"--stdio",
				"--login",
			],
			invocation.Arguments);
	}

	[Fact]
	public void WindowsNpxWrapsTheExactPackageAndReleaseAgeOverride() {
		string systemDirectory = Path.Combine(Path.GetTempPath(), "Windows", "System32");
		var invocation = AcpProcessInvocation.WrapWindowsNpx(
			@"C:\Program Files\node\npx.cmd",
			["--yes", "--no-audit", "--no-fund", "--no-update-notifier", "--min-release-age=0", "--", "@scope/agent@1.2.3", "--stdio"],
			systemDirectory);

		Assert.Equal(Path.Combine(systemDirectory, "cmd.exe"), invocation.Command);
		Assert.Equal(
			[
				"/d",
				"/s",
				"/v:off",
				"/c",
				"\"C:\\Program Files\\node\\npx.cmd\" --yes --no-audit --no-fund --no-update-notifier --min-release-age=0 -- @scope/agent@1.2.3 --stdio",
			],
			invocation.Arguments);
	}
}
