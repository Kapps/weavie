using Weavie.Core.Diagnostics;
using Weavie.TestSupport;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class HostLogRetentionTests {
	[Fact]
	public void KeepsNewestTwentyExitedLogsAndAllActiveOrUncertainLogs() {
		using var directory = new TempDirectory();
		var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		string[] completed = [.. Enumerable.Range(0, 25).Select(index => {
			string path = directory.WriteFile(Name(stamp.AddMinutes(index), 100 + index), "completed");
			File.SetLastWriteTimeUtc(path, stamp.AddMinutes(index));
			return path;
		})];
		string[] preserved = [
			directory.WriteFile(Name(stamp, 1), "active"),
			directory.WriteFile(Name(stamp, 2), "unknown owner"),
			directory.WriteFile("host-not-a-timestamp-100.log", "unknown name"),
			directory.WriteFile("host-20260101-000000-0000000-invalid.log", "unknown pid"),
			directory.WriteFile("last-crash.log", "crash"),
			directory.WriteFile("previous-crash.log", "previous crash"),
			directory.WriteFile("last-exit.log", "exit"),
		];
		foreach (string path in preserved) File.SetLastWriteTimeUtc(path, stamp.AddDays(-1));

		HostLogRetention.Prune(directory.Path, pid => pid switch { 1 => true, 2 => null, _ => false });

		Assert.Equal(completed.Skip(5).Concat(preserved).Order(), Directory.GetFiles(directory.Path).Order());
	}

	[Fact]
	public void OpeningPersistentLogPreservesEveryLogWithALivePid() {
		using var directory = new TempDirectory();
		var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		for (int index = 0; index < 25; index++) {
			directory.WriteFile(Name(stamp.AddMinutes(index), Environment.ProcessId), "active");
		}
		string current = directory.Combine(Name(DateTime.UtcNow, Environment.ProcessId));
		using (var sink = new FileLogSink(current)) {
			sink.Append("still writing");
			Assert.Empty(sink.Failure);
			Assert.Contains("still writing", File.ReadAllText(current), StringComparison.Ordinal);
		}
		Assert.Equal(26, Directory.GetFiles(directory.Path).Length);
	}

	[Fact]
	public void CleanupFailureIsVisibleWithoutStoppingPersistentLogging() {
		using var directory = new TempDirectory();
		string path = directory.Combine("host.log");
		using var sink = new FileLogSink(path, _ => throw new IOException("cleanup denied"));
		var buffer = new LogBuffer(10, sink);

		buffer.Append("still writing");

		Assert.Contains("cleanup denied", buffer.PersistenceFailure, StringComparison.Ordinal);
		Assert.Contains(buffer.Snapshot().Lines, line => line.Contains("cleanup denied", StringComparison.Ordinal));
		Assert.Contains("still writing", File.ReadAllText(path), StringComparison.Ordinal);
	}

	private static string Name(DateTime timestamp, int pid) =>
		$"{HostLogRetention.Prefix}{timestamp.ToString(HostLogRetention.TimestampFormat, System.Globalization.CultureInfo.InvariantCulture)}-{pid}.log";
}
