using System.Runtime.InteropServices;
using System.Text.Json;
using Weavie.AcpDistribution;
using Weavie.Core;
using Xunit;

namespace Weavie.WorktreeServe.Tests;

public sealed class AcpDistributionSnapshotTests : IDisposable {
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
	private readonly TempDirectory _root = new("acp-distribution-snapshot-tests");

	[Fact]
	public void Binary_installations_are_independent_and_keep_their_executable_mode() {
		string source = _root.CreateDirectory("production");
		string destination = _root.CreateDirectory("preview");
		string install = Directory.CreateDirectory(Path.Combine(
			Under(source, WeaviePaths.AcpPackages),
			"sample",
			"1.0.0",
			PlatformTarget())).FullName;
		string sourceCommand = Path.Combine(install, OperatingSystem.IsWindows() ? "sample.exe" : "sample");
		File.WriteAllText(sourceCommand, "production");
		if (!OperatingSystem.IsWindows()) {
			File.SetUnixFileMode(
				sourceCommand,
				UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		}
		WriteInstallations(source, [Launch("sample", sourceCommand, "binary")]);

		var projected = AcpDistributionSnapshot.Materialize(source, destination);

		var agent = Assert.Single(projected);
		Assert.StartsWith(Under(destination, WeaviePaths.AcpPackages), agent.Command, StringComparison.Ordinal);
		Assert.True(File.Exists(agent.Command));
		File.WriteAllText(agent.Command, "preview");
		Assert.Equal("production", File.ReadAllText(sourceCommand));
		if (!OperatingSystem.IsWindows()) {
			Assert.Equal(File.GetUnixFileMode(sourceCommand), File.GetUnixFileMode(agent.Command));
		}
		Assert.Equal(agent.Command, Assert.Single(AcpDistributionSnapshot.ReadCatalog(destination)).Command);
	}

	[Fact]
	public void Binary_installations_reject_links_without_materializing_a_catalog() {
		string source = _root.CreateDirectory("production");
		string destination = _root.CreateDirectory("preview");
		string install = Directory.CreateDirectory(Path.Combine(
			Under(source, WeaviePaths.AcpPackages),
			"sample",
			"1.0.0",
			PlatformTarget())).FullName;
		string sourceCommand = Path.Combine(install, OperatingSystem.IsWindows() ? "sample.exe" : "sample");
		File.WriteAllText(sourceCommand, "production");
		File.CreateSymbolicLink(Path.Combine(install, "linked"), sourceCommand);
		WriteInstallations(source, [Launch("sample", sourceCommand, "binary")]);

		Assert.Throws<InvalidOperationException>(() => AcpDistributionSnapshot.Materialize(source, destination));

		Assert.False(File.Exists(Under(destination, WeaviePaths.AcpInstallationsFile)));
	}

	[Fact]
	public void Package_recipes_and_custom_agents_round_trip_without_copying_external_commands() {
		string source = _root.CreateDirectory("production");
		string destination = _root.CreateDirectory("preview");
		WriteInstallations(source, [Launch("sample", "npx", "npx") with {
			Arguments = ["--yes", "sample-acp@1.0.0"],
		}]);
		Write(
			Under(source, WeaviePaths.AcpCustomAgentsFile),
			"""{"version":1,"agents":[{"id":"custom","name":"Custom","command":"custom-acp","args":["serve"],"env":{"MODE":"acp"}}]}""");

		var projected = AcpDistributionSnapshot.Materialize(source, destination);

		Assert.Equal(["sample", "custom"], projected.Select(agent => agent.Id));
		Assert.Equal("npx", projected[0].Command);
		Assert.Equal("custom-acp", projected[1].Command);
		Assert.Equal("acp", projected[1].Environment["MODE"]);
	}

	private static AcpLaunchSpec Launch(string id, string command, string distribution) => new() {
		Id = id,
		Name = "Sample",
		Version = "1.0.0",
		Command = command,
		Arguments = [],
		Environment = new Dictionary<string, string>(StringComparer.Ordinal),
		Distribution = distribution,
	};

	private static void WriteInstallations(string root, IReadOnlyList<AcpLaunchSpec> agents) => Write(
		Under(root, WeaviePaths.AcpInstallationsFile),
		JsonSerializer.Serialize(new { version = 1, agents }, JsonOptions));

	private static string PlatformTarget() {
		string os = OperatingSystem.IsWindows() ? "windows"
			: OperatingSystem.IsMacOS() ? "darwin"
			: "linux";
		string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
		return $"{os}-{architecture}";
	}

	private static string Under(string root, string canonicalPath) =>
		Path.Combine(root, Path.GetRelativePath(WeaviePaths.Root, canonicalPath));

	private static void Write(string path, string contents) {
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, contents);
	}

	public void Dispose() => _root.Dispose();
}
