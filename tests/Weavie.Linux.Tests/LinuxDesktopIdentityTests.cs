using Weavie.Linux.Hosting;
using Xunit;

namespace Weavie.Linux.Tests;

public sealed class LinuxDesktopIdentityTests {
	[Fact]
	public void EnsureInstalled_InvalidatesTheIconCacheOnlyWhenTheIconChanges() {
		string root = Directory.CreateTempSubdirectory().FullName;
		try {
			string appDirectory = Path.Combine(root, "app");
			string dataHome = Path.Combine(root, "data");
			Directory.CreateDirectory(appDirectory);
			File.WriteAllText(Path.Combine(appDirectory, "io.github.kapps.weavie.desktop"), "desktop");
			string iconSource = Path.Combine(appDirectory, "weavie.png");
			File.WriteAllBytes(iconSource, [1, 2, 3, 4]);
			LinuxDesktopIdentity.EnsureInstalled(appDirectory, dataHome);
			string iconRoot = Path.Combine(dataHome, "icons");
			var staleTimestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			Directory.SetLastWriteTimeUtc(iconRoot, staleTimestamp);

			LinuxDesktopIdentity.EnsureInstalled(appDirectory, dataHome);

			Assert.Equal(staleTimestamp, Directory.GetLastWriteTimeUtc(iconRoot));
			File.WriteAllBytes(iconSource, [5, 6, 7, 8]);

			LinuxDesktopIdentity.EnsureInstalled(appDirectory, dataHome);

			Assert.True(Directory.GetLastWriteTimeUtc(iconRoot) > staleTimestamp);
		} finally {
			Directory.Delete(root, recursive: true);
		}
	}
}
