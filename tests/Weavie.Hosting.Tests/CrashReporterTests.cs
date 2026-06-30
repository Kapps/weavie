using Weavie.Core;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// <see cref="CrashReporter"/> hands the next launch a prior run's crash report exactly once, rotating it aside so
/// it surfaces a single "exited unexpectedly" notice and keeps the detail for inspection.
/// </summary>
public sealed class CrashReporterTests {
	[Fact]
	public void TakePendingReport_ReturnsNull_WhenLastRunExitedCleanly() {
		if (File.Exists(WeaviePaths.LastCrashFile)) {
			File.Delete(WeaviePaths.LastCrashFile);
		}

		Assert.Null(CrashReporter.TakePendingReport());
	}

	[Fact]
	public void TakePendingReport_ReturnsReport_ThenRotatesSoItSurfacesOnce() {
		Directory.CreateDirectory(WeaviePaths.Logs);
		File.WriteAllText(WeaviePaths.LastCrashFile, "boom\nat Worker()");

		Assert.Equal("boom\nat Worker()", CrashReporter.TakePendingReport());
		Assert.False(File.Exists(WeaviePaths.LastCrashFile), "the live crash file should be rotated away");
		Assert.True(File.Exists(WeaviePaths.PreviousCrashFile), "the report should be retained for inspection");
		Assert.Null(CrashReporter.TakePendingReport());
	}
}
