using Weavie.Core.Agents;
using Weavie.Core.FileSystem;
using Weavie.Hosting.Agents;
using Weavie.Hosting.Messaging;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class AgentPaneWorkerTests {
	[Fact]
	public async Task OutputDrainPropagatesAnEarlierPublicationFailure() {
		await using var output = new AgentPaneOutput(new ThrowingTarget(), 0, _ => { });
		output.Live(new AgentPaneRecord(0, 1, 1, new AgentPaneMessage { Type = "started", ProviderId = "structured" }));

		var error = await Assert.ThrowsAsync<IOException>(() =>
			output.DrainAsync(CancellationToken.None));

		Assert.Equal("publish failed", error.Message);
	}

	[Fact]
	public async Task JournalDrainObservesAWorkerThatFailedDuringLoad() {
		var logs = new List<string>();
		var journal = new AgentPaneJournal(
			new ThrowingFileSystem(),
			"/transcript.json",
			_ => { },
			logs.Add);

		var error = await Assert.ThrowsAsync<IOException>(() =>
			journal.DrainAsync(CancellationToken.None));

		Assert.Equal("load failed", error.Message);
		Assert.Contains(logs, line => line.Contains("transcript worker failed", StringComparison.Ordinal));
		await Assert.ThrowsAsync<IOException>(() => journal.DisposeAsync().AsTask());
	}

	[Fact]
	public async Task JournalReadinessCompletesOnlyAfterThePersistedSnapshotIsSeeded() {
		using var release = new ManualResetEventSlim();
		IReadOnlyList<AgentPaneMessage>? loaded = null;
		await using var journal = new AgentPaneJournal(
			new BlockingTranscriptFileSystem(release),
			"/transcript.json",
			messages => loaded = messages,
			_ => { });
		var ready = journal.WaitUntilReadyAsync(CancellationToken.None);

		Assert.False(ready.IsCompleted);
		release.Set();
		await ready;

		var message = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<AgentPaneMessage>>(loaded));
		Assert.Equal("persisted", message.Text);
	}

	private sealed class ThrowingTarget : IMessageFeatureTarget {
		public void Publish<T>(string name, T payload) => throw new IOException("publish failed");

		public void PublishJson(string name, string payloadJson) => throw new IOException("publish failed");
	}

	private sealed class ThrowingFileSystem : IFileSystem {
		public bool FileExists(string path) => throw new IOException("load failed");

		public bool DirectoryExists(string path) => throw new NotSupportedException();

		public bool TryGetStat(string path, out FileStat stat) => throw new NotSupportedException();

		public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) => throw new NotSupportedException();

		public string ReadAllText(string path) => throw new NotSupportedException();

		public bool TryReadAllText(string path, out string contents) => throw new NotSupportedException();

		public byte[] ReadAllBytes(string path) => throw new NotSupportedException();

		public void WriteAllText(string path, string contents) => throw new NotSupportedException();

		public void WriteAllBytes(string path, byte[] contents) => throw new NotSupportedException();

		public void AppendAllText(string path, string contents) => throw new NotSupportedException();

		public void WriteAllTextAtomic(string path, string contents) => throw new NotSupportedException();

		public void DeleteFile(string path) => throw new NotSupportedException();
	}

	private sealed class BlockingTranscriptFileSystem(ManualResetEventSlim release) : IFileSystem {
		public bool FileExists(string path) => true;

		public string ReadAllText(string path) {
			release.Wait();
			return "{\"type\":\"item-completed\",\"providerId\":\"structured\",\"text\":\"persisted\"}\n";
		}

		public bool DirectoryExists(string path) => throw new NotSupportedException();

		public bool TryGetStat(string path, out FileStat stat) => throw new NotSupportedException();

		public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) => throw new NotSupportedException();

		public bool TryReadAllText(string path, out string contents) => throw new NotSupportedException();

		public byte[] ReadAllBytes(string path) => throw new NotSupportedException();

		public void WriteAllText(string path, string contents) => throw new NotSupportedException();

		public void WriteAllBytes(string path, byte[] contents) => throw new NotSupportedException();

		public void AppendAllText(string path, string contents) => throw new NotSupportedException();

		public void WriteAllTextAtomic(string path, string contents) => throw new NotSupportedException();

		public void DeleteFile(string path) => throw new NotSupportedException();
	}
}
