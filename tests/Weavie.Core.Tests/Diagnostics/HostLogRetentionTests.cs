using Weavie.Core.Diagnostics;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class HostLogRetentionTests {
	[Fact]
	public void KeepsOnlyTheMostRecentHostLogsAndLeavesOtherFilesAlone() {
		string dir = Path.Combine(Path.GetTempPath(), $"weavie-host-log-retention-{Guid.NewGuid():N}");
		Directory.CreateDirectory(dir);
		try {
			var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
			var expectedSurvivors = new List<string>();
			for (int i = 0; i < 25; i++) {
				string path = Path.Combine(dir, $"host-{i:D2}.log");
				File.WriteAllText(path, "line");
				File.SetLastWriteTimeUtc(path, stamp.AddMinutes(i));
				if (i >= 5) expectedSurvivors.Add(path); // the 20 most recently written
			}

			string unrelated = Path.Combine(dir, "last-crash.log");
			File.WriteAllText(unrelated, "crash");

			HostLogRetention.Prune(dir);

			Assert.Equal(
				expectedSurvivors.OrderBy(p => p).ToArray(),
				Directory.EnumerateFiles(dir, "host-*.log").OrderBy(p => p).ToArray());
			Assert.True(File.Exists(unrelated));
		} finally {
			Directory.Delete(dir, recursive: true);
		}
	}

	[Fact]
	public void ToleratesAMissingDirectory() {
		string dir = Path.Combine(Path.GetTempPath(), $"weavie-host-log-retention-missing-{Guid.NewGuid():N}");
		HostLogRetention.Prune(dir); // must not throw
	}
}
