using Weavie.Core.Diagnostics;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class PersistentLogTests {
	[Fact]
	public void CompletedLinesAreOnDiskBeforeTheWriterIsClosed() {
		string path = Path.Combine(Path.GetTempPath(), $"weavie-log-{Guid.NewGuid():N}", "host.log");
		using var sink = new FileLogSink(path);
		var buffer = new LogBuffer(10, sink);
		using var console = buffer.Tee(TextWriter.Null);
		console.WriteLine("[process] stopping child=42");
		Assert.Contains("[process] stopping child=42", File.ReadAllText(path), StringComparison.Ordinal);
		if (!OperatingSystem.IsWindows()) {
			Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
		}
	}

	[Fact]
	public void TrimsToTheCapOnceTheFileGrowsPastTwiceIt() {
		string path = Path.Combine(Path.GetTempPath(), $"weavie-log-{Guid.NewGuid():N}", "host.log");
		using var sink = new FileLogSink(path, capBytes: 100);
		for (int i = 0; i < 50; i++) {
			sink.Append($"line {i:D3}012345678901234567890123456789");
		}

		string content = File.ReadAllText(path);
		Assert.True(new FileInfo(path).Length <= 300, "expected the trim to keep the file near the cap");
		Assert.Contains("line 049", content, StringComparison.Ordinal);
		Assert.DoesNotContain("line 000", content, StringComparison.Ordinal);
	}

	[Fact]
	public void AnUnwritableLogRemainsVisibleInTheInAppBuffer() {
		string parent = Path.GetTempFileName();
		using var sink = new FileLogSink(Path.Combine(parent, "host.log"));
		var buffer = new LogBuffer(10, sink);
		buffer.Append("still running");
		Assert.NotEmpty(buffer.PersistenceFailure);
		Assert.Contains(buffer.Snapshot().Lines, line => line.Contains("Could not save logs", StringComparison.Ordinal));
		Assert.Contains("still running", buffer.Snapshot().Lines);
		File.Delete(parent);
	}
}
