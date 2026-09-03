using Weavie.Linux.Hosting;
using Xunit;

namespace Weavie.Linux.Tests;

public sealed class LinuxDesktopIdentityTests {
	[Fact]
	public void EnsureInstalled_InvalidatesTheIconCacheOnlyWhenTheIconChanges() {
		string root = Directory.CreateTempSubdirectory().FullName;
		try {
			string appDirectory = Path.Combine(root, "portable app");
			string dataHome = Path.Combine(root, "data");
			string executable = Path.Combine(appDirectory, "renamed-Weavie");
			Directory.CreateDirectory(appDirectory);
			File.WriteAllText(
				Path.Combine(appDirectory, "io.github.kapps.weavie.desktop"),
				"[Desktop Entry]\nExec=Weavie\n");
			string iconSource = Path.Combine(appDirectory, "weavie.png");
			File.WriteAllBytes(iconSource, [1, 2, 3, 4]);
			LinuxDesktopIdentity.EnsureInstalled(appDirectory, dataHome, executable);
			Assert.Contains(
				$"Exec=\"{executable}\"",
				File.ReadAllText(Path.Combine(dataHome, "applications", "io.github.kapps.weavie.desktop")),
				StringComparison.Ordinal);
			string iconRoot = Path.Combine(dataHome, "icons");
			var staleTimestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			Directory.SetLastWriteTimeUtc(iconRoot, staleTimestamp);

			LinuxDesktopIdentity.EnsureInstalled(appDirectory, dataHome, executable);

			Assert.Equal(staleTimestamp, Directory.GetLastWriteTimeUtc(iconRoot));
			File.WriteAllBytes(iconSource, [5, 6, 7, 8]);

			LinuxDesktopIdentity.EnsureInstalled(appDirectory, dataHome, executable);

			Assert.True(Directory.GetLastWriteTimeUtc(iconRoot) > staleTimestamp);
		} finally {
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void EnsureInstalled_EscapesARepresentableExecutablePath() {
		string root = Directory.CreateTempSubdirectory().FullName;
		try {
			string appDirectory = Path.Combine(root, "app");
			string dataHome = Path.Combine(root, "data");
			Directory.CreateDirectory(appDirectory);
			File.WriteAllText(
				Path.Combine(appDirectory, "io.github.kapps.weavie.desktop"),
				"[Desktop Entry]\nExec=Weavie\n");
			File.WriteAllBytes(Path.Combine(appDirectory, "weavie.png"), [1]);
			string executable = Path.Combine(appDirectory, "path\\with\"quote`dollar$percent% Weavie");

			LinuxDesktopIdentity.EnsureInstalled(appDirectory, dataHome, executable);

			Assert.Contains(
				"Exec=\"" + executable
					.Replace("\\", "\\\\\\\\", StringComparison.Ordinal)
					.Replace("\"", "\\\\\"", StringComparison.Ordinal)
					.Replace("`", "\\\\`", StringComparison.Ordinal)
					.Replace("$", "\\\\$", StringComparison.Ordinal)
					.Replace("%", "%%", StringComparison.Ordinal) + "\"",
				File.ReadAllText(Path.Combine(dataHome, "applications", "io.github.kapps.weavie.desktop")),
				StringComparison.Ordinal);
		} finally {
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void EnsureInstalled_PassesTheOpenedPathsAndStaysVisibleToTheChooser() {
		// A file manager's "Open With" list filters on g_app_info_should_show, which hides a NoDisplay entry —
		// and without %U the chosen paths never reach the process.
		string root = Directory.CreateTempSubdirectory().FullName;
		try {
			string appDirectory = Path.Combine(root, "app");
			string dataHome = Path.Combine(root, "data");
			Directory.CreateDirectory(appDirectory);
			File.WriteAllText(
				Path.Combine(appDirectory, "io.github.kapps.weavie.desktop"),
				"[Desktop Entry]\nExec=Weavie\nMimeType=text/plain;inode/directory;\n");
			File.WriteAllBytes(Path.Combine(appDirectory, "weavie.png"), [1]);

			LinuxDesktopIdentity.EnsureInstalled(appDirectory, dataHome, Path.Combine(appDirectory, "Weavie"));

			string installed = File.ReadAllText(
				Path.Combine(dataHome, "applications", "io.github.kapps.weavie.desktop"));
			Assert.EndsWith(" %U", installed.Split('\n').Single(line => line.StartsWith("Exec=", StringComparison.Ordinal)), StringComparison.Ordinal);
			Assert.Contains("MimeType=text/plain;inode/directory;", installed, StringComparison.Ordinal);
			Assert.DoesNotContain("NoDisplay", installed, StringComparison.Ordinal);
		} finally {
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void EnsureInstalled_LeavesAnotherInstallationsEntryAlone() {
		// A build run out of a source tree would otherwise take the file association with it, and leave the
		// installed Weavie unreachable from the desktop once that tree is deleted.
		string root = Directory.CreateTempSubdirectory().FullName;
		try {
			string appDirectory = Path.Combine(root, "app");
			string dataHome = Path.Combine(root, "data");
			Directory.CreateDirectory(appDirectory);
			File.WriteAllText(
				Path.Combine(appDirectory, "io.github.kapps.weavie.desktop"),
				"[Desktop Entry]\nExec=Weavie\n");
			File.WriteAllBytes(Path.Combine(appDirectory, "weavie.png"), [1]);

			string installedDirectory = Path.Combine(root, "installed");
			Directory.CreateDirectory(installedDirectory);
			string installed = Path.Combine(installedDirectory, "Weavie");
			File.WriteAllText(installed, "#!/bin/sh\n");
			LinuxDesktopIdentity.EnsureInstalled(appDirectory, dataHome, installed);

			LinuxDesktopIdentity.EnsureInstalled(
				appDirectory, dataHome, Path.Combine(appDirectory, "Weavie"));

			Assert.Contains(
				installed,
				File.ReadAllText(Path.Combine(dataHome, "applications", "io.github.kapps.weavie.desktop")),
				StringComparison.Ordinal);
		} finally {
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void EnsureInstalled_ReclaimsAnEntryWhoseWeavieIsGone() {
		string root = Directory.CreateTempSubdirectory().FullName;
		try {
			string appDirectory = Path.Combine(root, "app");
			string dataHome = Path.Combine(root, "data");
			Directory.CreateDirectory(appDirectory);
			File.WriteAllText(
				Path.Combine(appDirectory, "io.github.kapps.weavie.desktop"),
				"[Desktop Entry]\nExec=Weavie\n");
			File.WriteAllBytes(Path.Combine(appDirectory, "weavie.png"), [1]);
			Directory.CreateDirectory(Path.Combine(dataHome, "applications"));
			File.WriteAllText(
				Path.Combine(dataHome, "applications", "io.github.kapps.weavie.desktop"),
				"[Desktop Entry]\nExec=\"/gone/Weavie\" %U\n");

			string current = Path.Combine(appDirectory, "Weavie");
			LinuxDesktopIdentity.EnsureInstalled(appDirectory, dataHome, current);

			Assert.Contains(
				current,
				File.ReadAllText(Path.Combine(dataHome, "applications", "io.github.kapps.weavie.desktop")),
				StringComparison.Ordinal);
		} finally {
			Directory.Delete(root, recursive: true);
		}
	}

	[Theory]
	[InlineData("equals=Weavie")]
	[InlineData("control\nWeavie")]
	public void EnsureInstalled_RejectsAnUnrepresentableExecutablePath(string executableName) {
		string root = Directory.CreateTempSubdirectory().FullName;
		try {
			string appDirectory = Path.Combine(root, "app");
			Directory.CreateDirectory(appDirectory);
			File.WriteAllText(
				Path.Combine(appDirectory, "io.github.kapps.weavie.desktop"),
				"[Desktop Entry]\nExec=Weavie\n");
			File.WriteAllBytes(Path.Combine(appDirectory, "weavie.png"), [1]);

			Assert.Throws<InvalidDataException>(() => LinuxDesktopIdentity.EnsureInstalled(
				appDirectory,
				Path.Combine(root, "data"),
				Path.Combine(appDirectory, executableName)));
		} finally {
			Directory.Delete(root, recursive: true);
		}
	}
}
