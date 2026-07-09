using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests.Sessions;

/// <summary>Codex thread ids persist per worktree and load safely.</summary>
public sealed class CodexThreadStoreTests {
	private const string StorePath = "/weavie-codex-thread-tests/codex-threads.json";

	[Fact]
	public void Resolve_WithoutThread_CreatesNewIntent() {
		var store = new CodexThreadStore(new InMemoryFileSystem(), StorePath);

		var launch = store.Resolve("/repo");

		Assert.False(launch.Resume);
		Assert.Null(launch.ThreadId);
	}

	[Fact]
	public void Adopt_ThenResolve_ResumesThread() {
		var fs = new InMemoryFileSystem();
		var store = new CodexThreadStore(fs, StorePath);
		store.Adopt("/repo", "thr_123");

		var launch = new CodexThreadStore(fs, StorePath).Resolve("/repo");

		Assert.True(launch.Resume);
		Assert.Equal("thr_123", launch.ThreadId);
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, "{ broken ");

		var store = new CodexThreadStore(fs, StorePath);

		Assert.False(store.Resolve("/repo").Resume);
		Assert.True(fs.FileExists(StorePath + ".bad"));
	}
}
