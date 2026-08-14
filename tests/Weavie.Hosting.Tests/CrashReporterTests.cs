using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// <see cref="CrashReporter"/> hands the next launch a prior run's crash report exactly once, rotating it aside so
/// it surfaces a single "exited unexpectedly" notice and keeps the detail for inspection. Each test uses its own
/// private temp path (never <c>WeaviePaths.LastCrashFile</c>): that real path is also touched by every other
/// integration test's hello handshake (<c>HostCore.SurfacePriorCrash</c>) via a concurrently-running
/// <c>TestHost</c>, and sharing it here raced their file rotations against this test's (flaked 2026-08-14 ~01:58
/// UTC, https://github.com/Kapps/weavie/actions/runs/31762186704 — Actual: null, because a concurrent host's own
/// pending-report check consumed/rotated the shared file first). Fixed by giving every host (real and test) its
/// own injected crash-file paths instead of one process-wide static location.
/// </summary>
public sealed class CrashReporterTests {
	private static (string LastCrashFile, string PreviousCrashFile) IsolatedPaths() {
		string dir = Path.Combine(Path.GetTempPath(), "weavie-crash-reporter-tests-" + Guid.NewGuid().ToString("n"));
		return (Path.Combine(dir, "last-crash.log"), Path.Combine(dir, "previous-crash.log"));
	}

	[Fact]
	public void TakePendingReport_ReturnsNull_WhenLastRunExitedCleanly() {
		var (lastCrashFile, previousCrashFile) = IsolatedPaths();

		Assert.Null(CrashReporter.TakePendingReport(lastCrashFile, previousCrashFile));
	}

	[Fact]
	public void TakePendingReport_ReturnsReport_ThenRotatesSoItSurfacesOnce() {
		var (lastCrashFile, previousCrashFile) = IsolatedPaths();
		Directory.CreateDirectory(Path.GetDirectoryName(lastCrashFile)!);
		File.WriteAllText(lastCrashFile, "boom\nat Worker()");

		Assert.Equal("boom\nat Worker()", CrashReporter.TakePendingReport(lastCrashFile, previousCrashFile));
		Assert.False(File.Exists(lastCrashFile), "the live crash file should be rotated away");
		Assert.True(File.Exists(previousCrashFile), "the report should be retained for inspection");
		Assert.Null(CrashReporter.TakePendingReport(lastCrashFile, previousCrashFile));
	}
}
