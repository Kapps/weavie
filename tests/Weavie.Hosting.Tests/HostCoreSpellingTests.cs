using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>Exercises the projection-safe HostCore bridge over the real embedded spell checker and dictionaries.</summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSpellingTests {
	private static string Msg(object value) => JsonSerializer.Serialize(value);

	[Fact]
	public async Task Check_MapsCoreUtf16OffsetsIntoMonacoColumns() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Clear();

		host.Send(Msg(new {
			type = "spell-check",
			requestId = "check-1",
			modelEpoch = "model-1",
			path = Path.Combine(host.RepoRoot, "readme.txt"),
			languageId = "plaintext",
			lines = new[] { new { anchorId = "line-1", text = "teh" } },
		}));

		var result = await Wait.ForAsync(() => ReplyByRequest(host, "spell-check-result", "check-1"));
		var issue = Assert.Single(result.GetProperty("issues").EnumerateArray());
		Assert.Equal("line-1", issue.GetProperty("anchorId").GetString());
		Assert.Equal(1, issue.GetProperty("startColumn").GetInt32());
		Assert.Equal(4, issue.GetProperty("endColumn").GetInt32());
		Assert.Equal("teh", issue.GetProperty("word").GetString());
		Assert.Equal("model-1", result.GetProperty("modelEpoch").GetString());
		Assert.Equal("en-US", result.GetProperty("locale").GetString());
		Assert.False(result.TryGetProperty("error", out _));
	}

	[Fact]
	public async Task Check_RejectsAStaleProjectionBeforeItReachesTheChecker() {
		await using var host = await TestHost.StartAsync();
		var stale = host.Bridge.LastOfType("set-editor-session")!.Value.Clone();
		Assert.True((await host.CreateSessionAsync("feature")).Ok);
		host.Bridge.Clear();

		host.Bridge.Receive(Msg(new {
			type = "spell-check",
			requestId = "stale",
			modelEpoch = "model-1",
			path = Path.Combine(host.RepoRoot, "readme.txt"),
			languageId = "plaintext",
			lines = new[] { new { anchorId = "line-1", text = "teh" } },
			sessionId = stale.GetProperty("sessionId").GetString(),
			projectionEpoch = stale.GetProperty("projectionEpoch").GetString(),
			projectionRevision = stale.GetProperty("projectionRevision").GetInt64(),
			projectionPageId = stale.GetProperty("projectionPageId").GetString(),
		}));

		Assert.Empty(host.Bridge.PostedOfType("spell-check-result"));
	}

	[Fact]
	public async Task BackgroundProjectDictionaryChange_DoesNotInvalidateTheActiveSessionRequest() {
		await using var host = await TestHost.StartAsync();
		string primaryId = host.PrimaryId;
		Assert.True((await host.CreateSessionAsync("background-dictionary")).Ok);
		var background = host.Core.ActiveSessionForTest()!;
		host.Send(Msg(new { type = "switch-session", id = primaryId }));
		host.Bridge.Clear();

		// The long valid prefix guarantees this request cannot complete before B changes its own dictionary.
		string text = string.Join(' ', Enumerable.Repeat("the", 20_000)) + " teh";
		host.Send(Msg(new {
			type = "spell-check",
			requestId = "active-check",
			modelEpoch = "model-1",
			path = Path.Combine(host.RepoRoot, "readme.txt"),
			languageId = "plaintext",
			lines = new[] { new { anchorId = "line-1", text } },
		}));

		background.ProjectDictionary.Add("backgroundonlyword");

		var result = await Wait.ForAsync(() => ReplyByRequest(host, "spell-check-result", "active-check"));
		Assert.Contains(result.GetProperty("issues").EnumerateArray(), issue =>
			issue.GetProperty("word").GetString() == "teh");
	}

	[Fact]
	public async Task AddWord_WritesTheRequestedScopeAndInvalidatesTheMountedProjection() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		host.Bridge.Clear();

		host.Send(Msg(new {
			type = "spell-add-word",
			requestId = "project-word",
			word = "foobarquux",
			scope = "project",
		}));

		var projectResult = await Wait.ForAsync(() => ReplyByRequest(host, "spell-add-word-result", "project-word"));
		Assert.False(projectResult.TryGetProperty("error", out _));
		Assert.Contains("foobarquux", File.ReadAllLines(session.ProjectDictionary.FilePath));
		var projectChanged = await Wait.ForAsync(() => DictionaryChanged(host, "project"));
		Assert.Equal(session.Id, projectChanged.GetProperty("sessionId").GetString());

		host.Bridge.Clear();
		host.Send(Msg(new {
			type = "spell-add-word",
			requestId = "user-word",
			word = "quuxfoobar",
			scope = "user",
		}));

		var userResult = await Wait.ForAsync(() => ReplyByRequest(host, "spell-add-word-result", "user-word"));
		Assert.False(userResult.TryGetProperty("error", out _));
		Assert.Contains("quuxfoobar", File.ReadAllLines(host.UserDictionaryPath));
		await Wait.ForAsync(() => DictionaryChanged(host, "user"));
	}

	[Fact]
	public async Task Restore_UsesTheSuccessfulProviderReadAndReturnsOnlyAuthoredLines() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		host.Bridge.Clear();

		host.Send(Msg(new { type = "fs-read", id = "seed", path }));
		host.Send(Msg(new { type = "fs-write", id = "write", path, content = "hello\nteh\n" }));
		host.Bridge.Clear();
		host.Send(Msg(new { type = "fs-read", id = "read", path }));

		var read = host.Bridge.LastOfType("fs-read-result")!.Value;
		Assert.True(read.GetProperty("ok").GetBoolean());
		Assert.Equal("hello\nteh\n", read.GetProperty("content").GetString());
		Assert.Equal([new Weavie.Core.Changes.AuthoredLine(2, "teh")], session.AuthoredLines.Snapshot(path)!.Lines);

		host.Send(Msg(new { type = "spell-restore", requestId = "restore", modelEpoch = "model-1", path }));

		var restored = await Wait.ForAsync(() => ReplyByRequest(host, "spell-restore-result", "restore"));
		Assert.Equal("model-1", restored.GetProperty("modelEpoch").GetString());
		Assert.True(restored.GetProperty("version").GetInt64() > 0);
		var restoredLine = Assert.Single(restored.GetProperty("lines").EnumerateArray());
		Assert.Equal(2, restoredLine.GetProperty("line").GetInt32());
		Assert.Equal("teh", restoredLine.GetProperty("text").GetString());
	}

	[Fact]
	public async Task MissingProviderRead_ForgetsAuthoredLines() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		string path = Path.Combine(host.RepoRoot, "readme.txt");

		host.Send(Msg(new { type = "fs-read", id = "seed", path }));
		host.Send(Msg(new { type = "fs-write", id = "write", path, content = "hello\nteh\n" }));
		Assert.NotNull(session.AuthoredLines.Snapshot(path));

		File.Delete(path);
		host.Send(Msg(new { type = "fs-read", id = "missing", path }));

		Assert.Null(session.AuthoredLines.Snapshot(path));
	}

	[Fact]
	public async Task Restore_RejectsStaleProjectionAndOutOfScopePaths() {
		await using var host = await TestHost.StartAsync();
		var stale = host.Bridge.LastOfType("set-editor-session")!.Value.Clone();
		Assert.True((await host.CreateSessionAsync("restore-owner")).Ok);
		host.Bridge.Clear();

		host.Bridge.Receive(Msg(new {
			type = "spell-restore",
			requestId = "stale",
			modelEpoch = "model-1",
			path = Path.Combine(host.RepoRoot, "readme.txt"),
			sessionId = stale.GetProperty("sessionId").GetString(),
			projectionEpoch = stale.GetProperty("projectionEpoch").GetString(),
			projectionRevision = stale.GetProperty("projectionRevision").GetInt64(),
			projectionPageId = stale.GetProperty("projectionPageId").GetString(),
		}));

		Assert.Empty(host.Bridge.PostedOfType("spell-restore-result"));

		var session = host.Core.ActiveSessionForTest()!;
		string outside = Path.Combine(Path.GetTempPath(), "weavie-restore-secret.txt");
		session.AuthoredLines.OnWrite(outside, "secret");
		host.Send(Msg(new { type = "spell-restore", requestId = "outside", modelEpoch = "model-1", path = outside }));

		Assert.Empty(host.Bridge.PostedOfType("spell-restore-result"));
	}

	[Fact]
	public async Task ScratchSaveAndDiscard_TransferOrForgetAuthoredLines() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		string scratch = session.Scratch.CreateNew();
		session.AuthoredLines.OnRead(scratch, "seed\n");
		const string content = "seed\nteh\n";

		host.Send(Msg(new { type = "save-scratch-named", path = scratch, name = "saved.txt", content }));

		string saved = Path.Combine(host.RepoRoot, "saved.txt");
		Assert.Null(session.AuthoredLines.Snapshot(scratch));
		Assert.Equal([new Weavie.Core.Changes.AuthoredLine(2, "teh")], session.AuthoredLines.Snapshot(saved)!.Lines);

		string discarded = session.Scratch.CreateNew();
		session.AuthoredLines.OnWrite(discarded, "discard me");
		host.Send(Msg(new { type = "discard-scratch", path = discarded }));

		Assert.Null(session.AuthoredLines.Snapshot(discarded));
	}

	[Fact]
	public async Task LocaleSetting_PushesTheNewResolvedConfiguration() {
		await using var host = await TestHost.StartAsync();
		host.Bridge.Clear();

		host.Settings.Set(SpellSettings.Locale, JsonSerializer.SerializeToElement("en-GB"));

		var settings = await Wait.ForAsync(() => host.Bridge.LastOfType("spell-settings"));
		Assert.True(settings.GetProperty("enabled").GetBoolean());
		Assert.Equal("en-GB", settings.GetProperty("locale").GetString());
	}

	[Fact]
	public async Task MalformedProjectDictionary_DoesNotBlockStartupAndSurfacesAfterReady() {
		await using var host = await TestHost.StartAsync(repo => {
			string directory = Path.Combine(repo, ".weavie");
			Directory.CreateDirectory(directory);
			File.WriteAllText(Path.Combine(directory, "dictionary.txt"), "not a word\n");
		}, sendReady: false);
		var session = host.Core.ActiveSessionForTest()!;

		Assert.NotNull(session.ProjectDictionary.LastLoadError);
		host.Bridge.Clear();
		host.Send("""{"type":"ready"}""");

		var malformed = await Wait.ForAsync(() => DictionaryNotice(host, "project"));
		Assert.Equal("error", malformed.GetProperty("level").GetString());
		Assert.Contains("last valid words remain active", malformed.GetProperty("message").GetString());

		host.Bridge.Clear();
		File.WriteAllText(session.ProjectDictionary.FilePath, string.Empty);
		session.ProjectDictionary.Reload();

		var repaired = await Wait.ForAsync(() => DictionaryNotice(host, "project"));
		Assert.Equal("info", repaired.GetProperty("level").GetString());
		Assert.Null(session.ProjectDictionary.LastLoadError);
	}

	private static JsonElement? ReplyByRequest(TestHost host, string type, string requestId) {
		foreach (var message in host.Bridge.PostedOfType(type)) {
			if (message.GetProperty("requestId").GetString() == requestId) {
				return message;
			}
		}

		return null;
	}

	private static JsonElement? DictionaryChanged(TestHost host, string scope) {
		foreach (var message in host.Bridge.PostedOfType("spell-dictionary-changed")) {
			if (message.GetProperty("scope").GetString() == scope) {
				return message;
			}
		}

		return null;
	}

	private static JsonElement? DictionaryNotice(TestHost host, string scope) {
		string key = $"spell-dictionary-{scope}-malformed";
		foreach (var message in host.Bridge.PostedOfType("notify")) {
			if (message.TryGetProperty("key", out var messageKey) && messageKey.GetString() == key) {
				return message;
			}
		}

		return null;
	}
}
