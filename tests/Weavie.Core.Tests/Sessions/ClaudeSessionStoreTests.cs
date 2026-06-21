using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="ClaudeSessionStore"/> over the in-memory filesystem: first launch assigns an id and
/// creates the session (<c>--session-id</c>); a directory resumes only once <see cref="ClaudeSessionStore.MarkStarted"/>
/// confirms it came up; an unconfirmed create is re-created rather than resumed; distinct directories get
/// distinct ids; confirmed assignments survive reload while unconfirmed ones do not; a failed resume re-creates
/// the same id fresh; a forgotten (poison) id is dropped so the next launch mints a fresh one, converging
/// within two relaunches; and a malformed file is backed up + reset.
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
	public void Resolve_AfterMarkStarted_ResumesSameId() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		var first = store.Resolve(Cwd);
		store.MarkStarted(Cwd);
		var second = store.Resolve(Cwd);

		Assert.Equal(first.SessionId, second.SessionId);
		Assert.False(first.Resume);
		Assert.True(second.Resume);
	}

	[Fact]
	public void Resolve_CreateNeverConfirmed_RecreatesInsteadOfResuming() {
		// A launch that died before the session existed (e.g. a bad PATH) recorded only an assigned id; it must
		// be re-created under the same id, not resumed.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		var first = store.Resolve(Cwd);   // create launch ...
		var second = store.Resolve(Cwd);  // ... that never reported MarkStarted

		Assert.Equal(first.SessionId, second.SessionId);
		Assert.False(second.Resume);
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
	public void Resolve_ConfirmedThenReloaded_ResumesSameId() {
		var fs = new InMemoryFileSystem();
		var initial = new ClaudeSessionStore(fs, StorePath);
		string id = initial.Resolve(Cwd).SessionId;
		initial.MarkStarted(Cwd);

		var reloaded = new ClaudeSessionStore(fs, StorePath).Resolve(Cwd);

		Assert.Equal(id, reloaded.SessionId);
		Assert.True(reloaded.Resume); // a fresh process resumes the confirmed conversation
	}

	[Fact]
	public void Resolve_UnconfirmedThenReloaded_DoesNotResume() {
		var fs = new InMemoryFileSystem();
		new ClaudeSessionStore(fs, StorePath).Resolve(Cwd); // minted but never MarkStarted

		var reloaded = new ClaudeSessionStore(fs, StorePath).Resolve(Cwd);

		Assert.False(reloaded.Resume); // an id that never came up is re-created, not resumed
	}

	[Fact]
	public void MarkResumeFailed_RecreatesSameIdFresh() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string id = store.Resolve(Cwd).SessionId;
		store.MarkStarted(Cwd);
		Assert.True(store.Resolve(Cwd).Resume); // confirmed → resume mode

		store.MarkResumeFailed(Cwd);
		var afterFailure = store.Resolve(Cwd);

		Assert.Equal(id, afterFailure.SessionId); // identity stays stable
		Assert.False(afterFailure.Resume);        // but re-created with --session-id
		store.MarkStarted(Cwd);
		Assert.True(store.Resolve(Cwd).Resume);    // resumes again once re-confirmed
	}

	[Fact]
	public void Forget_MintsAFreshIdNextTime() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string poison = store.Resolve(Cwd).SessionId;
		store.MarkStarted(Cwd);

		store.Forget(Cwd);
		var afterForget = store.Resolve(Cwd);

		Assert.NotEqual(poison, afterForget.SessionId); // poison id gone, a fresh one minted
		Assert.False(afterForget.Resume);               // cold-starts with --session-id
	}

	[Fact]
	public void Forget_UnknownDirectory_IsNoOp() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		store.Forget(Cwd); // never resolved: nothing to remove, must not throw

		Assert.False(store.Resolve(Cwd).Resume);
	}

	[Fact]
	public void Forget_SurvivesReload() {
		var fs = new InMemoryFileSystem();
		var initial = new ClaudeSessionStore(fs, StorePath);
		string poison = initial.Resolve(Cwd).SessionId;
		initial.MarkStarted(Cwd);
		initial.Forget(Cwd);

		var reloaded = new ClaudeSessionStore(fs, StorePath).Resolve(Cwd);

		Assert.NotEqual(poison, reloaded.SessionId); // removal was persisted
	}

	[Fact]
	public void PoisonId_RecoversToAFreshSessionWithinTwoRelaunches() {
		// A confirmed id whose conversation is gone and whose id even a --session-id re-create can't reclaim. The
		// recovery (MarkResumeFailed on a failed resume, then Forget on a failed re-create) must converge on a
		// fresh, resumable session rather than loop.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string poison = store.Resolve(Cwd).SessionId;
		store.MarkStarted(Cwd);

		// Relaunch 1: --resume <poison> fails (conversation gone) -> RecreateSameId.
		var resume = store.Resolve(Cwd);
		Assert.True(resume.Resume);
		Assert.Equal(poison, resume.SessionId);
		store.MarkResumeFailed(Cwd);

		// Relaunch 2: --session-id <poison> also fails (id still held) -> ForgetId.
		var recreate = store.Resolve(Cwd);
		Assert.False(recreate.Resume);
		Assert.Equal(poison, recreate.SessionId);
		store.Forget(Cwd);

		// Relaunch 3: a fresh id cold-starts — claude has never seen it, so it comes up.
		var fresh = store.Resolve(Cwd);
		Assert.False(fresh.Resume);
		Assert.NotEqual(poison, fresh.SessionId);
		store.MarkStarted(Cwd);

		Assert.True(store.Resolve(Cwd).Resume); // resumes the recovered session thereafter
	}

	[Fact]
	public void Clear_DropsTrackedId_NextLaunchColdStarts() {
		// /clear then quit abandons the id, so a relaunch cold-starts rather than resuming the stale transcript
		// the clear was meant to escape.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string cleared = store.Resolve(Cwd).SessionId;
		store.MarkStarted(Cwd);
		Assert.True(store.Resolve(Cwd).Resume); // confirmed → would resume

		store.Clear(Cwd);
		var afterClear = store.Resolve(Cwd);

		Assert.False(afterClear.Resume);                 // a clear forces a cold start
		Assert.NotEqual(cleared, afterClear.SessionId);  // on a fresh id, not the cleared one
	}

	[Fact]
	public void Clear_ThenReloaded_ColdStarts() {
		var fs = new InMemoryFileSystem();
		var initial = new ClaudeSessionStore(fs, StorePath);
		string cleared = initial.Resolve(Cwd).SessionId;
		initial.MarkStarted(Cwd);
		initial.Clear(Cwd);

		var reloaded = new ClaudeSessionStore(fs, StorePath).Resolve(Cwd);

		Assert.False(reloaded.Resume);                 // clear was persisted: a fresh process cold-starts
		Assert.NotEqual(cleared, reloaded.SessionId);
	}

	[Fact]
	public void Adopt_OnFreshDirectory_ResumesThatIdNextLaunch() {
		// claude rotated to its own id, which Weavie adopts so the next launch resumes that conversation.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		store.Adopt(Cwd, "claude-rotated-id");
		var launch = store.Resolve(Cwd);

		Assert.True(launch.Resume);
		Assert.Equal("claude-rotated-id", launch.SessionId);
	}

	[Fact]
	public void ClearThenAdopt_ResumesTheAdoptedConversation() {
		// /clear then send a message: the stale id is dropped and the id claude settled on is adopted, so the
		// next launch resumes the post-clear conversation, not the long one the clear escaped.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string stale = store.Resolve(Cwd).SessionId;
		store.MarkStarted(Cwd);

		store.Clear(Cwd);
		store.Adopt(Cwd, "post-clear-id");
		var launch = store.Resolve(Cwd);

		Assert.True(launch.Resume);
		Assert.Equal("post-clear-id", launch.SessionId);
		Assert.NotEqual(stale, launch.SessionId);
	}

	[Fact]
	public void Adopt_RepointsAnExistingId() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		store.Resolve(Cwd);
		store.MarkStarted(Cwd);

		store.Adopt(Cwd, "different-id");

		Assert.Equal("different-id", store.Resolve(Cwd).SessionId);
	}

	[Fact]
	public void Adopt_MatchingStartedId_DoesNotRewriteFile() {
		// claude stays on the assigned id, so every UserPromptSubmit re-adopts the same started id; that must be
		// a no-op, never thrashing the persisted file.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string id = store.Resolve(Cwd).SessionId;
		store.MarkStarted(Cwd);
		string afterStart = fs.ReadAllText(StorePath);

		store.Adopt(Cwd, id);

		Assert.Equal(afterStart, fs.ReadAllText(StorePath)); // identical bytes — no rewrite
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
