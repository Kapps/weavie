using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="ClaudeSessionStore"/> over the in-memory filesystem: a first launch assigns an id
/// and creates the session (<c>--session-id</c>); later launches reattach (<c>--resume</c>); distinct
/// directories get distinct ids; the assignment survives reload; a failed resume re-creates the same id
/// fresh; and a malformed file is backed up + reset.
/// </summary>
public sealed class ClaudeSessionStoreTests {
	private const string StorePath = "/weavie-claude-session-tests/claude-sessions.json";
	private const string Cwd = "/repo/project";
	private const string OtherCwd = "/repo/other";

	[Fact]
	public void Resolve_FirstTime_AssignsIdAndStartsFresh() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		var launch = store.Resolve(Cwd);

		Assert.False(launch.Resume);
		Assert.True(Guid.TryParse(launch.SessionId, out _));
	}

	[Fact]
	public void Resolve_SameDirAgain_ResumesSameId() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		var first = store.Resolve(Cwd);
		var second = store.Resolve(Cwd);

		Assert.Equal(first.SessionId, second.SessionId);
		Assert.False(first.Resume);
		Assert.True(second.Resume);
	}

	[Fact]
	public void Resolve_DifferentDirs_GetDistinctIds() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		var a = store.Resolve(Cwd);
		var b = store.Resolve(OtherCwd);

		Assert.NotEqual(a.SessionId, b.SessionId);
	}

	[Fact]
	public void Resolve_PersistsAcrossReload_ResumesSameId() {
		var fs = new InMemoryFileSystem();
		string id = new ClaudeSessionStore(fs, StorePath).Resolve(Cwd).SessionId;

		var reloaded = new ClaudeSessionStore(fs, StorePath).Resolve(Cwd);

		Assert.Equal(id, reloaded.SessionId);
		Assert.True(reloaded.Resume); // a brand-new process resumes the previous conversation
	}

	[Fact]
	public void MarkResumeFailed_RecreatesSameIdFresh() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string id = store.Resolve(Cwd).SessionId;
		Assert.True(store.Resolve(Cwd).Resume); // now in resume mode

		store.MarkResumeFailed(Cwd);
		var afterFailure = store.Resolve(Cwd);

		Assert.Equal(id, afterFailure.SessionId); // identity stays stable
		Assert.False(afterFailure.Resume);        // but re-created with --session-id
		Assert.True(store.Resolve(Cwd).Resume);    // and resumes again thereafter
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, "{ broken ");

		var store = new ClaudeSessionStore(fs, StorePath);

		Assert.True(fs.FileExists(StorePath + ".bad"));
		Assert.False(store.Resolve(Cwd).Resume); // reset to empty, so the next launch starts fresh
	}
}
