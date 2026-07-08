using Weavie.Hosting.Agents.Codex;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class CodexInstallResolverTests : IDisposable {
	private readonly string _dir = Path.Combine(Path.GetTempPath(), "weavie-codex-install-tests", Guid.NewGuid().ToString("N"));

	public CodexInstallResolverTests() {
		Directory.CreateDirectory(_dir);
	}

	public void Dispose() {
		try {
			Directory.Delete(_dir, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}

	[Fact]
	public void Resolve_PackagedCodex_UsesPackageRootAndResourcePathEntries() {
		string package = Path.Combine(_dir, "current");
		string bin = Path.Combine(package, "bin");
		string resources = Path.Combine(package, "codex-resources");
		string pathDir = Path.Combine(package, "codex-path");
		Directory.CreateDirectory(bin);
		Directory.CreateDirectory(resources);
		Directory.CreateDirectory(pathDir);
		string executable = Path.Combine(bin, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
		File.WriteAllText(executable, string.Empty);
		if (OperatingSystem.IsWindows()) {
			File.WriteAllText(Path.Combine(resources, "codex-windows-sandbox-setup.exe"), string.Empty);
		}

		File.WriteAllText(
			Path.Combine(package, "codex-package.json"),
			$$"""
			{
			  "layoutVersion": 1,
			  "entrypoint": "bin/{{Path.GetFileName(executable)}}",
			  "resourcesDir": "codex-resources",
			  "pathDir": "codex-path"
			}
			""");

		var launch = CodexInstallResolver.Resolve(executable, _dir);

		Assert.Equal(executable, launch.Command);
		Assert.Equal(package, launch.WorkingDirectory);
		Assert.Equal([resources, pathDir], launch.PathEntries);
	}

	[Fact]
	public void Resolve_UnpackagedCodex_UsesWorkspaceAsWorkingDirectory() {
		string executable = Path.Combine(_dir, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
		File.WriteAllText(executable, string.Empty);

		var launch = CodexInstallResolver.Resolve(executable, _dir);

		Assert.Equal(executable, launch.Command);
		Assert.Equal(_dir, launch.WorkingDirectory);
		Assert.Empty(launch.PathEntries);
	}

	[Fact]
	public void Resolve_MissingCodex_FailsWithVisibleSettingReason() {
		var error = Assert.Throws<InvalidOperationException>(
			() => CodexInstallResolver.Resolve(Path.Combine(_dir, "missing-codex"), _dir));

		Assert.Contains("could not find", error.Message, StringComparison.Ordinal);
		Assert.Contains("missing-codex", error.Message, StringComparison.Ordinal);
	}
}
