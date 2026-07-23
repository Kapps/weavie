using System.Text.Json;
using Weavie.Core;
using Weavie.Core.Workspaces;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>Durable review journeys across host restart and explicit worktree-session unload.</summary>
[Collection(TestCollections.HostIntegration)]
public sealed class PersistentReviewTests {
	[Fact]
	public async Task SavedUserEdit_RestoresWithTheReview_AndSurvivesRejectAfterRestart() {
		await using var host = await TestHost.StartAsync(repo => CommitContent(repo, "a\nb\nc\n"));
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		File.WriteAllText(path, "a\nB\nc\n");
		host.Send("""{"type":"diff-against","ref":"HEAD"}""");
		await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));

		string saved = "note\na\nB\nc\n";
		host.Send(JsonSerializer.Serialize(new { type = "fs-write", id = "save", path, content = saved }));
		Assert.True(Assert.IsType<JsonElement>(host.Bridge.LastOfType("fs-write-result")).GetProperty("ok").GetBoolean());

		await host.RestartAsync();

		var changes = Assert.IsType<JsonElement>(host.Bridge.LastOfType("turn-changes"));
		Assert.Equal("vs HEAD", changes.GetProperty("label").GetString());
		Assert.Equal(path, Assert.Single(changes.GetProperty("files").EnumerateArray()).GetProperty("path").GetString());

		host.Send(JsonSerializer.Serialize(new { type = "revert-file", path }));

		Assert.Equal("note\na\nb\nc\n", File.ReadAllText(path));
	}

	[Fact]
	public async Task KeptHunk_RestoresAcrossUnload_AndRemainsKeptWhenTheFileIsRejected() {
		await using var host = await TestHost.StartAsync(repo => CommitContent(repo, "one\ntwo\nthree\nfour\nfive\n"));
		Assert.True((await host.CreateSessionAsync("durable-review")).Ok);
		var session = Assert.IsType<HostSession>(host.Core.ActiveSessionForTest());
		string path = Path.Combine(session.WorkspaceRoot, "readme.txt");
		File.WriteAllText(path, "ONE\ntwo\nthree\nfour\nFIVE\n");
		host.Send("""{"type":"diff-against","ref":"HEAD"}""");
		await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));
		host.Send(JsonSerializer.Serialize(new {
			type = "keep-hunk",
			path,
			baselineStart = 1,
			baselineEndExclusive = 2,
			currentStart = 1,
			currentEndExclusive = 2,
			guardText = "ONE",
		}));

		Assert.True((await host.Core.UnloadSessionAsync("durable-review", CancellationToken.None)).Ok);
		Assert.True((await host.Core.LoadSessionAsync("durable-review", CancellationToken.None)).Ok);
		host.Bridge.Clear();
		host.Send("""{"type":"switch-session","id":"durable-review"}""");

		var restored = await Wait.ForAsync(() => host.Bridge.LastOfType("turn-diff"));
		Assert.Equal("one\ntwo\nthree\nfour\nfive\n", restored.GetProperty("acceptedBaseline").GetString());
		Assert.Equal("ONE\ntwo\nthree\nfour\nfive\n", restored.GetProperty("baseline").GetString());
		Assert.Equal("ONE\ntwo\nthree\nfour\nFIVE\n", restored.GetProperty("current").GetString());

		host.Send(JsonSerializer.Serialize(new { type = "revert-file", path }));

		Assert.Equal("ONE\ntwo\nthree\nfour\nfive\n", File.ReadAllText(path));
	}

	[Fact]
	public async Task FileChangedWhileClosed_IsInvalidatedAndReportedInsteadOfRebased() {
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		File.WriteAllText(path, "hello\nreviewed\n");
		host.Send("""{"type":"diff-against","ref":"HEAD"}""");
		await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));

		await host.RestartAsync(() => File.WriteAllText(path, "unrelated replacement\n"));

		Assert.Null(host.Bridge.LastOfType("turn-changes"));
		var toast = Assert.IsType<JsonElement>(host.Bridge.LastOfType("notify"));
		Assert.Equal("warn", toast.GetProperty("level").GetString());
		Assert.Contains("invalidated", toast.GetProperty("message").GetString(), StringComparison.Ordinal);
		Assert.Null(host.Core.ActiveSessionForTest()!.Changes.ActiveReviewIdentity);
	}

	[Fact]
	public async Task UnloadPreservesCheckpoint_AndSuccessfulDeleteClearsIt() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("review-lifecycle")).Ok);
		var session = Assert.IsType<HostSession>(host.Core.ActiveSessionForTest());
		string path = Path.Combine(session.WorkspaceRoot, "readme.txt");
		string checkpoint = WeaviePaths.WorkspaceReviewCheckpointFile(
			host.Core.Id,
			WorkspaceId.ForPath(session.WorkspaceRoot).Value);
		File.WriteAllText(path, "changed\n");
		host.Bridge.Clear();
		host.Send("""{"type":"diff-against","ref":"HEAD"}""");
		await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));
		Assert.True(File.Exists(checkpoint));

		Assert.True((await host.Core.UnloadSessionAsync("review-lifecycle", CancellationToken.None)).Ok);
		Assert.True(File.Exists(checkpoint));

		var deleted = await host.Core.DeleteSessionAsync("review-lifecycle", force: true, CancellationToken.None);

		Assert.True(deleted.Ok, deleted.Error);
		Assert.False(File.Exists(checkpoint));
	}

	[Fact]
	public async Task UnloadWaitsForTheEditorFlush_AndKeepsTheOutgoingReviewRoutable() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("review-flush")).Ok);
		var session = Assert.IsType<HostSession>(host.Core.ActiveSessionForTest());
		string path = Path.Combine(session.WorkspaceRoot, "readme.txt");
		File.WriteAllText(path, "changed\n");
		host.Send("""{"type":"diff-against","ref":"HEAD"}""");
		await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));

		host.AutoMountEditorProjection = false;
		host.Bridge.Clear();
		var unload = host.Core.UnloadSessionAsync("review-flush", CancellationToken.None);
		await Wait.ForAsync(() => host.Bridge.LastOfType("set-editor-session"));
		Assert.False(unload.IsCompleted);

		string saved = "typed while switching\nchanged\n";
		host.Send(JsonSerializer.Serialize(new { type = "fs-write", id = "flush", path, content = saved }));
		var write = Assert.IsType<JsonElement>(host.Bridge.LastOfType("fs-write-result"));
		Assert.True(write.GetProperty("ok").GetBoolean());
		host.MountEditorProjection();

		Assert.True((await unload).Ok);
		Assert.Equal(saved, File.ReadAllText(path));
		Assert.True((await host.Core.LoadSessionAsync("review-flush", CancellationToken.None)).Ok);
		host.AutoMountEditorProjection = true;
		host.Send("""{"type":"switch-session","id":"review-flush"}""");
		var restored = Assert.IsType<HostSession>(host.Core.ActiveSessionForTest());
		Assert.Equal(path, Assert.Single(restored.Changes.TurnChanges()).Path);
	}

	[Fact]
	public async Task PageDisconnectCompletesAnUnloadWaitingForTheIncomingProjection() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("review-disconnect")).Ok);
		host.AutoMountEditorProjection = false;
		host.Bridge.Clear();

		var unload = host.Core.UnloadSessionAsync("review-disconnect", CancellationToken.None);
		await Wait.ForAsync(() => host.Bridge.LastOfType("set-editor-session"));
		Assert.False(unload.IsCompleted);

		host.Bridge.DisconnectPage(TestHost.TestPageId);

		Assert.True((await unload).Ok);
	}

	[Fact]
	public async Task SwitchingToACleanSession_ClearsTheOutgoingReviewProblem() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.CreateSessionAsync("invalid-review")).Ok);
		var session = Assert.IsType<HostSession>(host.Core.ActiveSessionForTest());
		string path = Path.Combine(session.WorkspaceRoot, "readme.txt");
		File.WriteAllText(path, "changed\n");
		host.Send("""{"type":"diff-against","ref":"HEAD"}""");
		await Wait.ForAsync(() => host.Bridge.LastOfType("turn-changes"));
		Assert.True((await host.Core.UnloadSessionAsync("invalid-review", CancellationToken.None)).Ok);
		File.WriteAllText(path, "changed outside Weavie\n");
		Assert.True((await host.Core.LoadSessionAsync("invalid-review", CancellationToken.None)).Ok);
		string primary = host.Bridge.LastOfType("session-list")!.Value.GetProperty("sessions")
			.EnumerateArray().Single(item => item.GetProperty("primary").GetBoolean())
			.GetProperty("id").GetString()!;

		host.Bridge.Clear();
		host.Send("""{"type":"switch-session","id":"invalid-review"}""");
		var problem = await Wait.ForAsync(() => host.Bridge.LastOfType("notify"));
		string key = problem.GetProperty("key").GetString()!;
		host.Bridge.Clear();

		host.Send(JsonSerializer.Serialize(new { type = "switch-session", id = primary }));

		var cleared = await Wait.ForAsync(() => host.Bridge.LastOfType("notify-clear"));
		Assert.Equal(key, cleared.GetProperty("key").GetString());
	}

	private static void CommitContent(string repo, string content) {
		File.WriteAllText(Path.Combine(repo, "readme.txt"), content);
		TestHost.RunGit(repo, "add", "-A");
		TestHost.RunGit(
			repo,
			"-c", "user.email=test@weavie.dev",
			"-c", "user.name=Weavie Test",
			"-c", "commit.gpgsign=false",
			"commit", "--quiet", "-m", "review baseline");
	}

}
