using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="ClaudeSessionStore"/> over the in-memory filesystem: a first launch assigns an id and
/// creates the session (<c>--session-id</c>); a directory is resumed only once <see cref="ClaudeSessionStore.MarkStarted"/>
/// confirms it came up; an unconfirmed create is re-created rather than resumed (the crux of the resume-loop
/// bug); distinct directories get distinct ids; confirmed assignments survive reload while unconfirmed ones do
/// not; a failed resume re-creates the same id fresh; a forgotten (poison) id is dropped so the next launch
/// mints a brand-new one — converging out of the reported crash loop within two relaunches; and a malformed
/// file is backed up + reset.
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
		// The resume-loop regression: a launch that died before the session existed (e.g. a bad PATH) recorded
		// only an assigned id — it must NOT be resumed later, it must be re-created under the same id.
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
		Assert.True(reloaded.Resume); // a brand-new process resumes the previously-confirmed conversation
	}

	[Fact]
	public void Resolve_UnconfirmedThenReloaded_DoesNotResume() {
		var fs = new InMemoryFileSystem();
		new ClaudeSessionStore(fs, StorePath).Resolve(Cwd); // minted but never MarkStarted

		var reloaded = new ClaudeSessionStore(fs, StorePath).Resolve(Cwd);

		Assert.False(reloaded.Resume); // an id that never came up is re-created, not resumed, after a restart
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
		Assert.True(store.Resolve(Cwd).Resume);    // and resumes again once re-confirmed
	}

	[Fact]
	public void Forget_MintsAFreshIdNextTime() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string poison = store.Resolve(Cwd).SessionId;
		store.MarkStarted(Cwd);

		store.Forget(Cwd);
		var afterForget = store.Resolve(Cwd);

		Assert.NotEqual(poison, afterForget.SessionId); // the poison id is gone — a brand-new one is minted
		Assert.False(afterForget.Resume);               // and it cold-starts with --session-id
	}

	[Fact]
	public void Forget_UnknownDirectory_IsNoOp() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		store.Forget(Cwd); // never resolved — nothing to remove, must not throw

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

		Assert.NotEqual(poison, reloaded.SessionId); // the removal was persisted
	}

	[Fact]
	public void PoisonId_RecoversToAFreshSessionWithinTwoRelaunches() {
		// The reported crash loop, replayed at the store level: a confirmed id whose conversation is gone, whose
		// id even a --session-id re-create can't reclaim. The controller's recovery (MarkResumeFailed on a failed
		// resume, then Forget on a failed re-create) must converge on a brand-new, resumable session — not loop.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string poison = store.Resolve(Cwd).SessionId;
		store.MarkStarted(Cwd);

		// Relaunch 1: --resume <poison> fails (conversation gone) -> heal as RecreateSameId.
		var resume = store.Resolve(Cwd);
		Assert.True(resume.Resume);
		Assert.Equal(poison, resume.SessionId);
		store.MarkResumeFailed(Cwd);

		// Relaunch 2: --session-id <poison> also fails (id still held) -> heal as ForgetId.
		var recreate = store.Resolve(Cwd);
		Assert.False(recreate.Resume);
		Assert.Equal(poison, recreate.SessionId);
		store.Forget(Cwd);

		// Relaunch 3: a fresh id is minted and cold-starts — claude has never seen it, so it comes up.
		var fresh = store.Resolve(Cwd);
		Assert.False(fresh.Resume);
		Assert.NotEqual(poison, fresh.SessionId);
		store.MarkStarted(Cwd);

		Assert.True(store.Resolve(Cwd).Resume); // and resumes the recovered session thereafter
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
