using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="ClaudeSessionStore"/> over the in-memory filesystem: it mints a stable id per directory
/// on first use and persists it; distinct directories get distinct ids; an assigned id survives reload; a
/// <see cref="ClaudeSessionStore.Adopt">adopt</see> repoints the id claude rotated to (and is a no-op when
/// unchanged); <see cref="ClaudeSessionStore.Clear"/> and <see cref="ClaudeSessionStore.Forget"/> drop the id so
/// the next resolve mints a fresh one; and a malformed file is backed up + reset. Whether a launch resumes or
/// re-creates the id is decided at launch from the transcript on disk, not by this store.
/// </summary>
public sealed class ClaudeSessionStoreTests {
	private const string StorePath = "/weavie-claude-session-tests/claude-sessions.json";
	private const string Cwd = "/repo/project";
	private const string OtherCwd = "/repo/other";

	[Fact]
	public void Resolve_FirstTime_MintsAnId() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		string id = store.Resolve(Cwd);

		Assert.True(Guid.TryParse(id, out _));
	}

	[Fact]
	public void Resolve_SecondTime_ReturnsTheSameId() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		Assert.Equal(store.Resolve(Cwd), store.Resolve(Cwd));
	}

	[Fact]
	public void Resolve_DifferentDirs_GetDistinctIds() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		Assert.NotEqual(store.Resolve(Cwd), store.Resolve(OtherCwd));
	}

	[Fact]
	public void Resolve_MintedId_SurvivesReload() {
		var fs = new InMemoryFileSystem();
		string id = new ClaudeSessionStore(fs, StorePath).Resolve(Cwd); // minting persists immediately

		Assert.Equal(id, new ClaudeSessionStore(fs, StorePath).Resolve(Cwd));
	}

	[Fact]
	public void Adopt_OnFreshDirectory_TracksThatId() {
		// claude rotated to its own id, which Weavie adopts so the next resolve returns that id.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		store.Adopt(Cwd, "claude-rotated-id");

		Assert.Equal("claude-rotated-id", store.Resolve(Cwd));
	}

	[Fact]
	public void Adopt_RepointsAnExistingId() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		store.Resolve(Cwd);

		store.Adopt(Cwd, "different-id");

		Assert.Equal("different-id", store.Resolve(Cwd));
	}

	[Fact]
	public void Adopt_MatchingId_DoesNotRewriteFile() {
		// claude stays on the assigned id, so every UserPromptSubmit re-adopts the same id; that must be a no-op,
		// never thrashing the persisted file.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string id = store.Resolve(Cwd);
		store.Adopt(Cwd, id);
		string afterAdopt = fs.ReadAllText(StorePath);

		store.Adopt(Cwd, id);

		Assert.Equal(afterAdopt, fs.ReadAllText(StorePath)); // identical bytes — no rewrite
	}

	[Fact]
	public void Adopt_SurvivesReload() {
		var fs = new InMemoryFileSystem();
		new ClaudeSessionStore(fs, StorePath).Adopt(Cwd, "adopted-id");

		Assert.Equal("adopted-id", new ClaudeSessionStore(fs, StorePath).Resolve(Cwd));
	}

	[Fact]
	public void Forget_MintsAFreshIdNextTime() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string poison = store.Resolve(Cwd);

		store.Forget(Cwd);

		Assert.NotEqual(poison, store.Resolve(Cwd)); // poison id gone, a fresh one minted
	}

	[Fact]
	public void Forget_UnknownDirectory_IsNoOp() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		store.Forget(Cwd); // never resolved: nothing to remove, must not throw

		Assert.True(Guid.TryParse(store.Resolve(Cwd), out _));
	}

	[Fact]
	public void Forget_SurvivesReload() {
		var fs = new InMemoryFileSystem();
		var initial = new ClaudeSessionStore(fs, StorePath);
		string poison = initial.Resolve(Cwd);
		initial.Forget(Cwd);

		Assert.NotEqual(poison, new ClaudeSessionStore(fs, StorePath).Resolve(Cwd)); // removal was persisted
	}

	[Fact]
	public void Clear_DropsTrackedId_NextLaunchMintsFresh() {
		// /clear then quit abandons the id, so a relaunch cold-starts rather than resuming the stale transcript
		// the clear was meant to escape.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string cleared = store.Resolve(Cwd);

		store.Clear(Cwd);

		Assert.NotEqual(cleared, store.Resolve(Cwd));
	}

	[Fact]
	public void Clear_ThenReloaded_MintsFresh() {
		var fs = new InMemoryFileSystem();
		var initial = new ClaudeSessionStore(fs, StorePath);
		string cleared = initial.Resolve(Cwd);
		initial.Clear(Cwd);

		Assert.NotEqual(cleared, new ClaudeSessionStore(fs, StorePath).Resolve(Cwd)); // clear was persisted
	}

	[Fact]
	public void ClearThenAdopt_TracksTheAdoptedId() {
		// /clear then send a message: the stale id is dropped and the id claude settled on is adopted, so the
		// next resolve returns the post-clear id, not the long one the clear escaped.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string stale = store.Resolve(Cwd);

		store.Clear(Cwd);
		store.Adopt(Cwd, "post-clear-id");

		Assert.Equal("post-clear-id", store.Resolve(Cwd));
		Assert.NotEqual(stale, store.Resolve(Cwd));
	}

	[Fact]
	public void MalformedFile_BacksUpAndResets() {
		var fs = new InMemoryFileSystem();
		fs.WriteAllText(StorePath, "{ broken ");

		var store = new ClaudeSessionStore(fs, StorePath);

		Assert.True(fs.FileExists(StorePath + ".bad"));
		Assert.True(Guid.TryParse(store.Resolve(Cwd), out _)); // reset to empty, so the next launch mints fresh
	}
}
