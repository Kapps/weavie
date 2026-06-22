using Weavie.Core.FileSystem;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>
/// Exercises <see cref="ClaudeSessionStore"/> over the in-memory filesystem: first launch assigns an id and
/// creates the session (<c>--session-id</c>); a directory resumes only once a user message has been
/// <see cref="ClaudeSessionStore.Adopt">adopted</see> off the hook stream (output volume alone never marks it
/// resumable); an un-messaged session is re-created rather than resumed; once adopted, the started flag is
/// durable so a later run resumes even without a new message; distinct directories get distinct ids; adopted
/// assignments survive reload while un-messaged ones do not; a failed resume re-creates the same id fresh; a
/// forgotten (poison) id is dropped so the next launch mints a fresh one, converging within two relaunches; and
/// a malformed file is backed up + reset.
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
	public void Resolve_AfterAdopt_ResumesSameId() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		var first = store.Resolve(Cwd);
		store.Adopt(Cwd, first.SessionId); // a user message confirmed off the hook stream
		var second = store.Resolve(Cwd);

		Assert.Equal(first.SessionId, second.SessionId);
		Assert.False(first.Resume);
		Assert.True(second.Resume);
	}

	[Fact]
	public void Resolve_NeverMessaged_RecreatesInsteadOfResuming() {
		// A session that came up but was never messaged (so claude wrote no transcript) recorded only an assigned
		// id; it must be re-created under the same id, not resumed — the "missing session" bug, where painting the
		// TUI was mistaken for a resumable conversation.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);

		var first = store.Resolve(Cwd);   // create launch ...
		var second = store.Resolve(Cwd);  // ... that never adopted a user message

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
	public void Resolve_AdoptedThenReloaded_ResumesSameId() {
		var fs = new InMemoryFileSystem();
		var initial = new ClaudeSessionStore(fs, StorePath);
		string id = initial.Resolve(Cwd).SessionId;
		initial.Adopt(Cwd, id);

		var reloaded = new ClaudeSessionStore(fs, StorePath).Resolve(Cwd);

		Assert.Equal(id, reloaded.SessionId);
		Assert.True(reloaded.Resume); // a fresh process resumes the adopted conversation
	}

	[Fact]
	public void Resolve_AdoptedThenResumedWithoutNewMessage_StaysResumable() {
		// A session messaged in a prior run stays resumable on later runs even when it is resumed and then
		// unloaded WITHOUT sending a new message. The started flag is durable (persisted), not re-derived per
		// launch from a fresh message — so resuming-without-messaging must not silently drop resumability.
		var fs = new InMemoryFileSystem();
		string id;
		// Run 1: open, send one message, quit.
		{
			var run1 = new ClaudeSessionStore(fs, StorePath);
			id = run1.Resolve(Cwd).SessionId;
			run1.Adopt(Cwd, id);
		}

		// Run 2: reopen → resumes; no new message is sent before unloading.
		var run2 = new ClaudeSessionStore(fs, StorePath);
		Assert.True(run2.Resolve(Cwd).Resume);

		// Run 3: reopen again → still resumes the same conversation.
		var run3 = new ClaudeSessionStore(fs, StorePath).Resolve(Cwd);
		Assert.True(run3.Resume);
		Assert.Equal(id, run3.SessionId);
	}

	[Fact]
	public void Resolve_UnmessagedThenReloaded_DoesNotResume() {
		var fs = new InMemoryFileSystem();
		new ClaudeSessionStore(fs, StorePath).Resolve(Cwd); // minted but never adopted a message

		var reloaded = new ClaudeSessionStore(fs, StorePath).Resolve(Cwd);

		Assert.False(reloaded.Resume); // an id that never held a conversation is re-created, not resumed
	}

	[Fact]
	public void MarkResumeFailed_RecreatesSameIdFresh() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string id = store.Resolve(Cwd).SessionId;
		store.Adopt(Cwd, id);
		Assert.True(store.Resolve(Cwd).Resume); // adopted → resume mode

		store.MarkResumeFailed(Cwd);
		var afterFailure = store.Resolve(Cwd);

		Assert.Equal(id, afterFailure.SessionId); // identity stays stable
		Assert.False(afterFailure.Resume);        // but re-created with --session-id
		store.Adopt(Cwd, id);
		Assert.True(store.Resolve(Cwd).Resume);    // resumes again once re-adopted
	}

	[Fact]
	public void Forget_MintsAFreshIdNextTime() {
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string poison = store.Resolve(Cwd).SessionId;
		store.Adopt(Cwd, poison);

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
		initial.Adopt(Cwd, poison);
		initial.Forget(Cwd);

		var reloaded = new ClaudeSessionStore(fs, StorePath).Resolve(Cwd);

		Assert.NotEqual(poison, reloaded.SessionId); // removal was persisted
	}

	[Fact]
	public void PoisonId_RecoversToAFreshSessionWithinTwoRelaunches() {
		// An adopted id whose transcript is present but unusable (so resume is attempted) yet whose id even a
		// --session-id re-create can't reclaim. The recovery (MarkResumeFailed on a failed resume, then Forget on
		// a failed re-create) must converge on a fresh, resumable session rather than loop.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string poison = store.Resolve(Cwd).SessionId;
		store.Adopt(Cwd, poison);

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
		store.Adopt(Cwd, fresh.SessionId);

		Assert.True(store.Resolve(Cwd).Resume); // resumes the recovered session thereafter
	}

	[Fact]
	public void Clear_DropsTrackedId_NextLaunchColdStarts() {
		// /clear then quit abandons the id, so a relaunch cold-starts rather than resuming the stale transcript
		// the clear was meant to escape.
		var fs = new InMemoryFileSystem();
		var store = new ClaudeSessionStore(fs, StorePath);
		string cleared = store.Resolve(Cwd).SessionId;
		store.Adopt(Cwd, cleared);
		Assert.True(store.Resolve(Cwd).Resume); // adopted → would resume

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
		initial.Adopt(Cwd, cleared);
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
		store.Adopt(Cwd, stale);

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
		string original = store.Resolve(Cwd).SessionId;
		store.Adopt(Cwd, original);

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
		store.Adopt(Cwd, id);
		string afterAdopt = fs.ReadAllText(StorePath);

		store.Adopt(Cwd, id);

		Assert.Equal(afterAdopt, fs.ReadAllText(StorePath)); // identical bytes — no rewrite
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
