using System.Text.Json;
using Weavie.Core.Configuration;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>Exercises host-owned whole-document spelling diagnostics and dictionary actions.</summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSpellingTests {
	private static string Msg(object value) => JsonSerializer.Serialize(value);

	[Fact]
	public async Task DocumentChanged_ChecksWholeDocumentAndEchoesBrowserDocumentRevision() {
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		const long documentRevision = 17;
		var projection = host.Bridge.LastOfType("set-editor-session")!.Value.Clone();
		host.Bridge.Clear();

		host.Send(Msg(new {
			type = "spell-document-changed",
			path,
			content = "teh\nThis line is correct.\nrecieve",
			documentRevision,
		}));

		var result = await Wait.ForAsync(() => Diagnostics(host, path, documentRevision));
		var issues = result.GetProperty("issues").EnumerateArray().ToArray();
		Assert.Equal(Path.GetFullPath(path), result.GetProperty("path").GetString());
		Assert.Equal(documentRevision, result.GetProperty("documentRevision").GetInt64());
		Assert.Contains(issues, issue =>
			issue.GetProperty("line").GetInt32() == 1
			&& issue.GetProperty("startColumn").GetInt32() == 1
			&& issue.GetProperty("endColumn").GetInt32() == 4
			&& issue.GetProperty("word").GetString() == "teh");
		Assert.Contains(issues, issue =>
			issue.GetProperty("line").GetInt32() == 3
			&& issue.GetProperty("word").GetString() == "recieve");
		Assert.All(issues, issue => Assert.False(issue.TryGetProperty("lineText", out _)));
		Assert.Equal("en-US", result.GetProperty("locale").GetString());
		Assert.False(result.TryGetProperty("error", out _));
		AssertProjection(projection, result);
	}

	[Fact]
	public async Task DocumentChanged_RequiresABrowserDocumentRevision() {
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		host.Bridge.Clear();

		host.Send(Msg(new {
			type = "spell-document-changed",
			path,
			content = "teh",
		}));
		host.Send(Msg(new {
			type = "spell-document-changed",
			path,
			content = "teh",
			documentRevision = -1,
		}));

		Assert.Empty(host.Bridge.PostedOfType("spell-diagnostics"));
	}

	[Fact]
	public async Task DocumentChanged_RejectsStaleProjectionAndOutsidePath() {
		await using var host = await TestHost.StartAsync();
		var stale = host.Bridge.LastOfType("set-editor-session")!.Value.Clone();
		Assert.True((await host.CreateSessionAsync("spell-owner")).Ok);
		host.Bridge.Clear();

		host.Bridge.Receive(Msg(new {
			type = "spell-document-changed",
			path = Path.Combine(host.RepoRoot, "readme.txt"),
			content = "teh",
			documentRevision = 1,
			sessionId = stale.GetProperty("sessionId").GetString(),
			projectionEpoch = stale.GetProperty("projectionEpoch").GetString(),
			projectionRevision = stale.GetProperty("projectionRevision").GetInt64(),
			projectionPageId = stale.GetProperty("projectionPageId").GetString(),
		}));
		host.Send(Msg(new {
			type = "spell-document-changed",
			path = Path.Combine(Path.GetTempPath(), "outside.txt"),
			content = "teh",
			documentRevision = 2,
		}));

		Assert.Empty(host.Bridge.PostedOfType("spell-diagnostics"));
	}

	[Fact]
	public async Task AddWord_PersistsScopesSignalsTheBrowserAndRequiresResubmission() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		const string content = "foobarquux quuxfoobar";
		var projection = host.Bridge.LastOfType("set-editor-session")!.Value.Clone();
		host.Send(Msg(new {
			type = "spell-document-changed",
			path,
			content,
			documentRevision = 4,
		}));
		await Wait.ForAsync(() => DiagnosticsWithIssueCount(host, path, documentRevision: 4, count: 2));
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
		var projectChanged = await Wait.ForAsync(() => host.Bridge.LastOfType("spell-dictionary-changed"));
		AssertProjection(projection, projectChanged);
		Assert.Empty(host.Bridge.PostedOfType("spell-diagnostics"));

		host.Send(Msg(new {
			type = "spell-document-changed",
			path,
			content,
			documentRevision = 5,
		}));
		await Wait.ForAsync(() => DiagnosticsWithIssueCount(host, path, documentRevision: 5, count: 1));
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
		var userChanged = await Wait.ForAsync(() => host.Bridge.LastOfType("spell-dictionary-changed"));
		AssertProjection(projection, userChanged);
		Assert.Empty(host.Bridge.PostedOfType("spell-diagnostics"));

		host.Send(Msg(new {
			type = "spell-document-changed",
			path,
			content,
			documentRevision = 6,
		}));
		await Wait.ForAsync(() => DiagnosticsWithIssueCount(host, path, documentRevision: 6, count: 0));
	}

	[Fact]
	public async Task SpellSettingChange_PushesConfigurationWithoutRecheckingTheDocument() {
		await using var host = await TestHost.StartAsync();
		string path = Path.Combine(host.RepoRoot, "readme.txt");
		host.Send(Msg(new {
			type = "spell-document-changed",
			path,
			content = "colour",
			documentRevision = 9,
		}));
		await Wait.ForAsync(() => Diagnostics(host, path, documentRevision: 9));
		host.Bridge.Clear();

		host.Settings.Set(SpellSettings.Locale, JsonSerializer.SerializeToElement("en-GB"));

		var settings = await Wait.ForAsync(() => host.Bridge.LastOfType("spell-settings"));
		Assert.True(settings.GetProperty("enabled").GetBoolean());
		Assert.Equal("en-GB", settings.GetProperty("locale").GetString());
		Assert.Empty(host.Bridge.PostedOfType("spell-diagnostics"));

		host.Send(Msg(new {
			type = "spell-document-changed",
			path,
			content = "colour",
			documentRevision = 10,
		}));
		var diagnostics = await Wait.ForAsync(() => Diagnostics(host, path, documentRevision: 10));
		Assert.Equal("en-GB", diagnostics.GetProperty("locale").GetString());
	}

	[Fact]
	public async Task ProjectionMount_ReplaysCurrentSettingsBeforeQueuedDiagnostics() {
		await using var host = await TestHost.StartAsync();
		host.Settings.Set(SpellSettings.Locale, JsonSerializer.SerializeToElement("en-GB"));
		host.AutoMountEditorProjection = false;
		Assert.True((await host.CreateSessionAsync("spell-settings-projection")).Ok);
		string path = Path.Combine(host.Core.ActiveSessionForTest()!.WorkspaceRoot, "readme.txt");
		host.Bridge.Clear();

		host.Send(Msg(new {
			type = "spell-document-changed",
			path,
			content = "colur",
			documentRevision = 11,
		}));
		await Wait.ForAsync(() => host.Core.PendingSpellOperationCountForTest == 0 ? true : (bool?)null);
		Assert.Empty(host.Bridge.PostedOfType("spell-settings"));
		Assert.Empty(host.Bridge.PostedOfType("spell-diagnostics"));

		host.MountEditorProjection();

		await Wait.ForAsync(() => host.Bridge.LastOfType("spell-diagnostics"));
		var spellingTypes = host.Bridge.Posted
			.Select(static json => JsonDocument.Parse(json).RootElement.GetProperty("type").GetString())
			.Where(static type => type is "spell-settings" or "spell-diagnostics");
		Assert.Equal(["spell-settings", "spell-diagnostics"], spellingTypes);
		var settings = Assert.Single(host.Bridge.PostedOfType("spell-settings"));
		Assert.True(settings.GetProperty("enabled").GetBoolean());
		Assert.Equal("en-GB", settings.GetProperty("locale").GetString());
		Assert.Equal("en-GB", host.Bridge.LastOfType("spell-diagnostics")!.Value.GetProperty("locale").GetString());

		host.Bridge.Clear();
		host.MountEditorProjection();
		Assert.Single(host.Bridge.PostedOfType("spell-settings"));
	}

	[Fact]
	public async Task LegacyProjection_QueuesSuggestionResultUntilMonacoMount() {
		await using var host = await TestHost.StartAsync(_ => { }, sendReady: false);
		host.Bridge.Receive("""{"type":"ready"}""");
		string sessionId = host.Bridge.LastOfType("set-editor-session")!.Value
			.GetProperty("sessionId").GetString()!;
		host.Bridge.Clear();

		host.Bridge.Receive(Msg(new {
			type = "spell-suggest",
			sessionId,
			requestId = "legacy-suggest",
			word = "teh",
		}));
		await Wait.ForAsync(() => host.Core.PendingSpellOperationCountForTest == 0 ? true : (bool?)null);
		Assert.Empty(host.Bridge.PostedOfType("spell-suggest-result"));

		host.Bridge.Receive("""{"type":"monaco-ready"}""");

		var result = await Wait.ForAsync(() =>
			ReplyByRequest(host, "spell-suggest-result", "legacy-suggest"));
		Assert.Contains("the", result.GetProperty("suggestions").EnumerateArray()
			.Select(static suggestion => suggestion.GetString()), StringComparer.OrdinalIgnoreCase);
		Assert.Equal(-1, result.GetProperty("projectionRevision").GetInt64());
	}

	[Fact]
	public async Task OperationTeardown_WaitsForSupersededWorkToFinishUnwinding() {
		await using var host = await TestHost.StartAsync();
		var session = host.Core.ActiveSessionForTest()!;
		var operations = new SpellOperationRegistry();
		var first = operations.Begin(session, "document", "/first");
		var second = operations.Begin(session, "document", "/first");
		Assert.NotNull(first);
		Assert.NotNull(second);

		Assert.True(first.Token.IsCancellationRequested);
		var stopping = operations.StopSessionAsync(session);
		Assert.True(second.Token.IsCancellationRequested);
		Assert.False(stopping.IsCompleted);
		Assert.Null(operations.Begin(session, "document", "/late"));

		operations.End(second);
		Assert.False(stopping.IsCompleted);
		operations.End(first);
		await stopping;
	}

	[Fact]
	public async Task DictionaryWriteTeardown_ClosesScopeBeforeAwaitingRegisteredWork() {
		object scope = new();
		var writes = new QuiescingTaskRegistry<object>();
		var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		bool lateWorkStarted = false;
		Assert.True(writes.TryRun(scope, async () => {
			started.TrySetResult();
			await release.Task;
		}));
		await started.Task;

		var stopping = writes.StopScopeAsync(scope);

		Assert.False(stopping.IsCompleted);
		Assert.False(writes.TryRun(scope, () => {
			lateWorkStarted = true;
			return Task.CompletedTask;
		}));
		release.TrySetResult();
		await stopping;
		Assert.False(lateWorkStarted);
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

	private static JsonElement? Diagnostics(TestHost host, string path, long documentRevision) =>
		Latest(host, "spell-diagnostics", message =>
			message.GetProperty("path").GetString() == Path.GetFullPath(path)
			&& message.GetProperty("documentRevision").GetInt64() == documentRevision);

	private static JsonElement? DiagnosticsWithIssueCount(TestHost host, string path, long documentRevision, int count) =>
		Latest(host, "spell-diagnostics", message =>
			message.GetProperty("path").GetString() == Path.GetFullPath(path)
			&& message.GetProperty("documentRevision").GetInt64() == documentRevision
			&& message.GetProperty("issues").GetArrayLength() == count);

	private static JsonElement? ReplyByRequest(TestHost host, string type, string requestId) =>
		Latest(host, type, message => message.GetProperty("requestId").GetString() == requestId);

	private static JsonElement? Latest(TestHost host, string type, Func<JsonElement, bool> predicate) {
		foreach (var message in host.Bridge.PostedOfType(type).Reverse()) {
			if (predicate(message)) {
				return message;
			}
		}

		return null;
	}

	private static JsonElement? DictionaryNotice(TestHost host, string scope) {
		string key = $"spell-dictionary-{scope}-malformed";
		return Latest(host, "notify", message =>
			message.TryGetProperty("key", out var messageKey) && messageKey.GetString() == key);
	}

	private static void AssertProjection(JsonElement projection, JsonElement message) {
		Assert.Equal(projection.GetProperty("sessionId").GetString(), message.GetProperty("sessionId").GetString());
		Assert.Equal(projection.GetProperty("projectionEpoch").GetString(), message.GetProperty("projectionEpoch").GetString());
		Assert.Equal(projection.GetProperty("projectionRevision").GetInt64(), message.GetProperty("projectionRevision").GetInt64());
		Assert.Equal(projection.GetProperty("projectionPageId").GetString(), message.GetProperty("projectionPageId").GetString());
	}
}
