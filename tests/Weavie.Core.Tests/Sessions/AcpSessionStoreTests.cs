using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class AcpSessionStoreTests {
	private const string StorePath = "/config/acp-sessions.json";
	private const string Workspace = "/workspace";

	[Fact]
	public void MalformedStore_FailsWithoutReplacingOriginalData() {
		var fileSystem = new InMemoryFileSystem([new(StorePath, "{ broken")]);
		var store = new AcpSessionStore(fileSystem, StorePath);

		var error = Assert.Throws<AcpSessionStoreException>(() => store.Resolve("acp", Workspace));

		Assert.Contains(StorePath, error.Message, StringComparison.Ordinal);
		Assert.Equal("{ broken", fileSystem.ReadAllText(StorePath));
	}

	[Fact]
	public void NullSessionEntry_FailsWithoutReplacingOriginalData() {
		const string malformed = "{\"version\":2,\"sessions\":[null]}";
		var fileSystem = new InMemoryFileSystem([new(StorePath, malformed)]);
		var store = new AcpSessionStore(fileSystem, StorePath);

		var error = Assert.Throws<AcpSessionStoreException>(() => store.Resolve("acp", Workspace));

		Assert.Contains("null entries", error.Message, StringComparison.Ordinal);
		Assert.Equal(malformed, fileSystem.ReadAllText(StorePath));
	}

	[Fact]
	public void FailedAtomicWrite_DoesNotMutateInMemoryAssociation() {
		var inner = new InMemoryFileSystem();
		var store = new AcpSessionStore(new FailingAtomicFileSystem(inner), StorePath);

		Assert.Throws<AcpSessionStoreException>(() => store.Adopt("acp", Workspace, "thread-1", 1));

		Assert.Null(store.Resolve("acp", Workspace));
		Assert.False(inner.FileExists(StorePath));
	}

	[Fact]
	public void Adoption_IsAvailableAfterReload() {
		var fileSystem = new InMemoryFileSystem();
		var store = new AcpSessionStore(fileSystem, StorePath);
		store.Adopt("acp", Workspace, "thread-1", 7);

		var reloaded = new AcpSessionStore(fileSystem, StorePath);

		Assert.Equal("thread-1", reloaded.Resolve("acp", Workspace));
		Assert.Equal(7, reloaded.ResolveTurnNumber("acp", Workspace));
	}

	[Fact]
	public void RootWorkspace_IsPreservedAcrossReload() {
		string root = Path.GetPathRoot(Path.GetFullPath(Workspace))
			?? throw new InvalidOperationException("The test filesystem has no root.");
		var fileSystem = new InMemoryFileSystem();
		var store = new AcpSessionStore(fileSystem, StorePath);
		store.Adopt("acp", root, "root-thread", 0);

		var reloaded = new AcpSessionStore(fileSystem, StorePath);

		Assert.Equal("root-thread", reloaded.Resolve("acp", root));
	}

	[Fact]
	public void CaseSensitiveHostsKeepDifferentlyCasedWorkspacesDistinct() {
		if (OperatingSystem.IsWindows()) return;
		var fileSystem = new InMemoryFileSystem();
		var store = new AcpSessionStore(fileSystem, StorePath);
		store.Adopt("acp", "/workspace/Foo", "upper-thread", 0);
		store.Adopt("acp", "/workspace/foo", "lower-thread", 0);

		var reloaded = new AcpSessionStore(fileSystem, StorePath);

		Assert.Equal("upper-thread", reloaded.Resolve("acp", "/workspace/Foo"));
		Assert.Equal("lower-thread", reloaded.Resolve("acp", "/workspace/foo"));
	}

	private sealed class FailingAtomicFileSystem(IFileSystem inner) : IFileSystem {
		public bool FileExists(string path) => inner.FileExists(path);

		public bool DirectoryExists(string path) => inner.DirectoryExists(path);

		public bool TryGetStat(string path, out FileStat stat) => inner.TryGetStat(path, out stat);

		public IReadOnlyList<DirectoryEntry> EnumerateDirectory(string path) => inner.EnumerateDirectory(path);

		public string ReadAllText(string path) => inner.ReadAllText(path);

		public bool TryReadAllText(string path, out string contents) => inner.TryReadAllText(path, out contents);

		public byte[] ReadAllBytes(string path) => inner.ReadAllBytes(path);

		public void WriteAllText(string path, string contents) => inner.WriteAllText(path, contents);

		public void WriteAllBytes(string path, byte[] contents) => inner.WriteAllBytes(path, contents);

		public void AppendAllText(string path, string contents) => inner.AppendAllText(path, contents);

		public void WriteAllTextAtomic(string path, string contents) =>
			throw new IOException("Atomic persistence denied.");

		public void DeleteFile(string path) => inner.DeleteFile(path);
	}
}
