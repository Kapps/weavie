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
