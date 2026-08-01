using System.Net;
using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// End-to-end tests for the Notion source (personal-access-token auth via the in-app dialog): connect opens the
/// token page + asks the page for the token, the pasted token is validated against a stubbed <c>GET /v1/users/me</c>
/// and saved, and fetch serves canned Notion API JSON. Proves the whole stack (web message → HostCore →
/// SourceConnector → validate/save/fetch → toast / source-doc) without the network.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreSourcesTests {
	private static string Msg(object value) => JsonSerializer.Serialize(value);

	[Fact]
	public async Task OpenTarget_NonSourceUrl_OpensADurableWebOverlay() {
		await using var host = await TestHost.StartAsync();

		Open(host, "https://example.com/page");

		var web = await Wait.ForAsync(() =>
			host.Bridge.LastEvent(host.SelectedSession.Address, "editor", "openOverlay"));
		Assert.Equal("https://example.com/page", web.GetProperty("path").GetString());
		Assert.Equal("web", web.GetProperty("kind").GetString());
		Assert.Null(SourceEvent(host, "document"));
	}

	[Fact]
	public async Task ConnectNotion_OpensTheTokenPageAndPromptsForTheToken() {
		await using var host = await TestHost.StartAsync();

		var result = await host.InvokeClientCommandAsync("weavie.source.connectNotion", new { });
		Assert.True(result.Ok, result.Error);

		var prompt = await Wait.ForAsync(() => SourceEvent(host, "promptToken"));
		Assert.Equal("notion", prompt.GetProperty("sourceId").GetString());
		Assert.Equal("https://app.notion.com/developers/tokens", host.Platform.LastOpenedUrl);
	}

	[Fact]
	public async Task SetSourceToken_ValidatesAndSavesTheToken() {
		await using var host = await TestHost.StartAsync();
		host.SourceHttp.Responder = _ => (HttpStatusCode.OK, """{ "bot": { "workspace_name": "Acme" } }""");

		var result = await SaveToken(host, "ntn_secret");

		var toast = await Wait.ForAsync(() => Notify(host, "info"));
		Assert.Contains("Acme", toast.GetProperty("message").GetString());
		Assert.True(result.GetProperty("ok").GetBoolean());
		string tokenFile = Path.Combine(host.SourcesDir, "notion.json");
		Assert.True(File.Exists(tokenFile));
		Assert.Contains("ntn_secret", File.ReadAllText(tokenFile));
		Assert.Contains(host.SourceHttp.Requests, r => r.RequestUri!.AbsoluteUri.Contains("/v1/users/me")
			&& r.Headers.Authorization is { Scheme: "Bearer", Parameter: "ntn_secret" });
	}

	[Fact]
	public async Task ConnectFromPalette_SuccessDoesNotReplayTheTokenPrompt() {
		await using var host = await TestHost.StartAsync();
		host.SourceHttp.Responder = _ => (HttpStatusCode.OK, """{ "bot": { "workspace_name": "Acme" } }""");
		Assert.True((await host.InvokeClientCommandAsync("weavie.source.connectNotion", new { })).Ok);
		await Wait.ForAsync(() => SourceEvent(host, "promptToken"));

		Assert.True((await SaveToken(host, "ntn_secret")).GetProperty("ok").GetBoolean());
		host.Bridge.Clear();
		await host.SessionRequestAsync<JsonElement>(
			host.SelectedSession,
			"lifecycle",
			"sync",
			new { });

		Assert.Null(SourceEvent(host, "promptToken"));
	}

	[Fact]
	public async Task DismissTokenPrompt_RemovesItsDurableState() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.InvokeClientCommandAsync("weavie.source.connectNotion", new { })).Ok);
		await Wait.ForAsync(() => SourceEvent(host, "promptToken"));

		host.SessionEvent(host.SelectedSession, "sources", "dismissToken", new { });
		host.Bridge.Clear();
		await host.SessionRequestAsync<JsonElement>(
			host.SelectedSession,
			"lifecycle",
			"sync",
			new { });

		Assert.Null(SourceEvent(host, "promptToken"));
	}

	[Fact]
	public async Task SetSourceToken_RejectedToken_RepliesInlineErrorAndDoesNotSave() {
		await using var host = await TestHost.StartAsync();
		host.SourceHttp.Responder = _ => (HttpStatusCode.Unauthorized, "{}");

		var result = await SaveToken(host, "bad");

		// The rejection comes back as an inline result (not a toast), so the dialog stays open for a correction.
		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Contains("rejected", result.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
		Assert.False(File.Exists(Path.Combine(host.SourcesDir, "notion.json"))); // an invalid token is never saved
	}

	[Fact]
	public async Task SetSourceToken_ValidateFaults_StillRepliesSoTheDialogNeverHangs() {
		await using var host = await TestHost.StartAsync();
		// HttpClient's own request timeout surfaces as TaskCanceledException — the dialog must still get a result.
		host.SourceHttp.Responder = _ => throw new TaskCanceledException();

		var result = await SaveToken(host, "ntn_x");

		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.False(File.Exists(Path.Combine(host.SourcesDir, "notion.json")));
	}

	[Fact]
	public async Task SourceFetch_AfterConnect_ReturnsTheMarkdownDoc() {
		await using var host = await TestHost.StartAsync();
		WriteToken(host, "ntn_secret");
		host.SourceHttp.Responder = request => request.RequestUri!.AbsoluteUri switch {
			// The markdown endpoint also lives under /pages/, so match it first.
			var u when u.Contains("/markdown") => (HttpStatusCode.OK, """{ "markdown": "Body **text**", "truncated": false, "unknown_block_ids": [] }"""),
			var u when u.Contains("/pages/") => (HttpStatusCode.OK, """{ "last_edited_time": "2026-06-30T06:15:48.000Z", "properties": { "Name": { "type": "title", "title": [ { "plain_text": "Spec" } ] } } }"""),
			_ => (HttpStatusCode.NotFound, "{}"),
		};

		Open(host, "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d");

		var doc = await Wait.ForAsync(() => SourceEvent(host, "document"));
		Assert.Equal("https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", doc.GetProperty("target").GetString());
		Assert.Equal("Spec", doc.GetProperty("title").GetString());
		Assert.Equal("Body **text**", doc.GetProperty("markdown").GetString()); // the single render + Claude channel
		Assert.Equal("2026-06-30T06:15:48.000Z", doc.GetProperty("editedTime").GetString()); // read from the page JSON
		Assert.Equal("notion", doc.GetProperty("sourceId").GetString()); // keys the tab icon web-side
		var loading = SourceEvent(host, "loading")!.Value;
		Assert.Equal("Spec", loading.GetProperty("title").GetString());
	}

	[Fact]
	public async Task ReconnectReplaysTheSourceDocumentAndItsEditorTab() {
		await using var host = await TestHost.StartAsync();
		WriteToken(host, "ntn_secret");
		host.SourceHttp.Responder = request => request.RequestUri!.AbsoluteUri switch {
			var u when u.Contains("/markdown") => (HttpStatusCode.OK, """{ "markdown": "Body", "truncated": false, "unknown_block_ids": [] }"""),
			var u when u.Contains("/pages/") => (HttpStatusCode.OK, """{ "properties": { "Name": { "type": "title", "title": [ { "plain_text": "Spec" } ] } } }"""),
			_ => (HttpStatusCode.NotFound, "{}"),
		};
		const string target = "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d";
		Open(host, target);
		await Wait.ForAsync(() => SourceEvent(host, "document"));
		host.Bridge.Clear();

		await host.SessionRequestAsync<JsonElement>(
			host.SelectedSession,
			"lifecycle",
			"sync",
			new { });

		var document = SourceEvent(host, "document");
		Assert.True(document.HasValue);
		Assert.Equal("Body", document!.Value.GetProperty("markdown").GetString());
		var restore = host.Bridge.LastEvent(host.SelectedSession.Address, "editor", "restore");
		Assert.Contains(
			restore!.Value.GetProperty("session").GetProperty("open").EnumerateArray(),
			entry => entry.GetProperty("path").GetString() == target
				&& entry.GetProperty("kind").GetString() == "source");
	}

	[Fact]
	public async Task OpenTarget_NotionUrlWithoutToken_RoutesToTheConnectPrompt() {
		await using var host = await TestHost.StartAsync();

		Open(host, "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d");

		// Not connected: instead of a blank tab or an easy-to-miss error toast, the user is sent to connect.
		var prompt = await Wait.ForAsync(() => SourceEvent(host, "promptToken"));
		Assert.Equal("notion", prompt.GetProperty("sourceId").GetString());
		Assert.Equal("https://app.notion.com/developers/tokens", host.Platform.LastOpenedUrl);
		Assert.Null(SourceEvent(host, "document"));
	}

	[Fact]
	public async Task OpenTarget_NotConnected_ThenConnect_OpensThePendingTarget() {
		await using var host = await TestHost.StartAsync();
		host.SourceHttp.Responder = request => request.RequestUri!.AbsoluteUri switch {
			var u when u.Contains("/users/me") => (HttpStatusCode.OK, """{ "bot": { "workspace_name": "Acme" } }"""),
			var u when u.Contains("/markdown") => (HttpStatusCode.OK, """{ "markdown": "Body text", "truncated": false, "unknown_block_ids": [] }"""),
			var u when u.Contains("/pages/") => (HttpStatusCode.OK, """{ "properties": { "Name": { "type": "title", "title": [ { "plain_text": "Spec" } ] } } }"""),
			_ => (HttpStatusCode.NotFound, "{}"),
		};

		// Open before connecting → routed to connect; then pasting a valid token opens the remembered page.
		Open(host, "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d");
		await Wait.ForAsync(() => SourceEvent(host, "promptToken"));
		Assert.True((await SaveToken(host, "ntn_secret")).GetProperty("ok").GetBoolean());

		var doc = await Wait.ForAsync(() => SourceEvent(host, "document"));
		Assert.Equal("https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", doc.GetProperty("target").GetString());
		Assert.Equal("Spec", doc.GetProperty("title").GetString());
	}

	[Fact]
	public async Task SourceFetch_Failure_PostsSourceErrorIntoTheTab() {
		await using var host = await TestHost.StartAsync();
		WriteToken(host, "ntn_secret");
		host.SourceHttp.Responder = _ => (HttpStatusCode.InternalServerError, "{}");

		Open(host, "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d");

		// The failure surfaces in the already-open tab (source-error keyed by target), not as a toast.
		var error = await Wait.ForAsync(() => SourceEvent(host, "error"));
		Assert.Equal("https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", error.GetProperty("target").GetString());
		Assert.NotEmpty(error.GetProperty("message").GetString()!);
		Assert.Null(SourceEvent(host, "document"));
	}

	[Fact]
	public async Task SourceFetch_NonJsonOkBody_PostsSourceError_NotAStuckSpinner() {
		await using var host = await TestHost.StartAsync();
		WriteToken(host, "ntn_secret");
		// A 200 carrying non-JSON (proxy / captive-portal / incident HTML) throws JsonException deep in the parse;
		// the eager spinner is already up, so it must still resolve to an error rather than spin forever.
		host.SourceHttp.Responder = _ => (HttpStatusCode.OK, "<html>not json</html>");

		Open(host, "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d");

		var error = await Wait.ForAsync(() => SourceEvent(host, "error"));
		Assert.Equal("https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", error.GetProperty("target").GetString());
		Assert.Null(SourceEvent(host, "document"));
	}

	[Fact]
	public async Task SourceFetch_TruncatedPage_FlagsTheDocAndKeepsTheMarkdownVerbatim() {
		await using var host = await TestHost.StartAsync();
		WriteToken(host, "ntn_secret");
		host.SourceHttp.Responder = request => request.RequestUri!.AbsoluteUri switch {
			var u when u.Contains("/markdown") => (HttpStatusCode.OK, """{ "markdown": "# Big page", "truncated": true, "unknown_block_ids": ["a"] }"""),
			var u when u.Contains("/pages/") => (HttpStatusCode.OK, """{ "properties": { "Name": { "type": "title", "title": [ { "plain_text": "Big" } ] } } }"""),
			_ => (HttpStatusCode.NotFound, "{}"),
		};

		Open(host, "https://www.notion.so/Big-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d");

		// The loss travels as flags beside the markdown (the web renders a banner), never inside it — the markdown
		// must stay the verbatim fetched text the edit path diffs against.
		var doc = await Wait.ForAsync(() => SourceEvent(host, "document"));
		Assert.Equal("# Big page", doc.GetProperty("markdown").GetString());
		Assert.True(doc.GetProperty("truncated").GetBoolean());
		Assert.Equal(1, doc.GetProperty("unknownBlocks").GetInt32());
	}

	[Fact]
	public async Task SaveSourceEdit_PatchesTheExactOpAndPushesTheRefreshedDoc() {
		await using var host = await TestHost.StartAsync();
		WriteToken(host, "ntn_secret");
		string? patchBody = null;
		HttpRequestMessage? patch = null;
		host.SourceHttp.Responder = request => {
			if (request.Method == HttpMethod.Patch) {
				patch = request;
				patchBody = request.Content!.ReadAsStringAsync().Result; // read while the request is still alive
				return (HttpStatusCode.OK, """{ "markdown": "Hello edited\nWorld", "truncated": false, "unknown_block_ids": [] }""");
			}

			return (HttpStatusCode.OK, """{ "last_edited_time": "2026-07-02T10:00:00.000Z", "properties": { "Name": { "type": "title", "title": [ { "plain_text": "Spec" } ] } } }""");
		};

		SaveEdit(
			host,
			"https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d",
			"Hello\n",
			"Hello edited\n");

		// The refreshed doc comes from the PATCH response's markdown, keeping the store in sync with Notion.
		var doc = await Wait.ForAsync(() => SourceEvent(host, "document"));
		Assert.Equal("Hello edited\nWorld", doc.GetProperty("markdown").GetString());
		Assert.Equal("Spec", doc.GetProperty("title").GetString());
		Assert.Equal("notion", doc.GetProperty("sourceId").GetString());
		// The PATCH itself: the markdown endpoint, authenticated, and EXACTLY one update_content op — no
		// replace_content, no allow_deleting_content, no replace_all_matches (their absence is the safety rail).
		Assert.NotNull(patch);
		Assert.EndsWith("/v1/pages/1a2b3c4d-5e6f-7a8b-9c0d-1e2f3a4b5c6d/markdown", patch!.RequestUri!.AbsoluteUri);
		Assert.Equal("Bearer", patch.Headers.Authorization!.Scheme);
		Assert.Equal("ntn_secret", patch.Headers.Authorization.Parameter);
		Assert.Equal("""{"type":"update_content","update_content":{"content_updates":[{"old_str":"Hello\n","new_str":"Hello edited\n"}]}}""", patchBody);
		Assert.Null(SourceEvent(host, "editError"));
	}

	[Fact]
	public async Task SaveSourceEdit_ValidationError_PostsAStaleEditError() {
		await using var host = await TestHost.StartAsync();
		WriteToken(host, "ntn_secret");
		host.SourceHttp.Responder = _ =>
			(HttpStatusCode.BadRequest, """{ "code": "validation_error", "message": "old_str did not match" }""");

		SaveEdit(
			host,
			"https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d",
			"gone\n",
			"new\n");

		// The page changed in Notion since the fetch: stale:true so the block offers a re-fetch; no doc is pushed.
		var error = await Wait.ForAsync(() => SourceEvent(host, "editError"));
		Assert.True(error.GetProperty("stale").GetBoolean());
		Assert.Contains("did not match", error.GetProperty("message").GetString());
		Assert.Null(SourceEvent(host, "document"));
	}

	[Fact]
	public async Task SaveSourceEdit_RequestValidationError_IsNotReportedAsStale() {
		await using var host = await TestHost.StartAsync();
		WriteToken(host, "ntn_secret");
		// A validation_error that isn't about the op's old_str (e.g. a malformed body) is a client bug, not a
		// stale page — stale:true would offer a re-fetch that can never help. The API's reason must surface.
		host.SourceHttp.Responder = _ =>
			(HttpStatusCode.BadRequest, """{ "code": "validation_error", "message": "body.type should be defined, instead was `undefined`." }""");

		SaveEdit(
			host,
			"https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d",
			"a\n",
			"b\n");

		var error = await Wait.ForAsync(() => SourceEvent(host, "editError"));
		Assert.False(error.GetProperty("stale").GetBoolean());
		Assert.Contains("body.type", error.GetProperty("message").GetString());
		Assert.Null(SourceEvent(host, "document"));
	}

	[Fact]
	public async Task SaveSourceEdit_ServerFailure_StillResolvesTheSavingState() {
		await using var host = await TestHost.StartAsync();
		WriteToken(host, "ntn_secret");
		host.SourceHttp.Responder = _ => (HttpStatusCode.InternalServerError, "{}");

		SaveEdit(
			host,
			"https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d",
			"a\n",
			"b\n");

		// Fire-and-forget like the fetch: every failure must resolve the block's saving state, never leave it stuck.
		var error = await Wait.ForAsync(() => SourceEvent(host, "editError"));
		Assert.False(error.GetProperty("stale").GetBoolean());
		Assert.NotEmpty(error.GetProperty("message").GetString()!);
		Assert.Null(SourceEvent(host, "document"));
	}

	private static void WriteToken(TestHost host, string token) {
		Directory.CreateDirectory(host.SourcesDir);
		File.WriteAllText(Path.Combine(host.SourcesDir, "notion.json"), Msg(new { token }));
	}

	private static void Open(TestHost host, string url) =>
		host.SessionEvent(host.SelectedSession, "sources", "open", new { url });

	private static Task<JsonElement> SaveToken(TestHost host, string token) =>
		host.SessionRequestAsync<JsonElement>(
			host.SelectedSession,
			"sources",
			"saveToken",
			new { sourceId = "notion", token });

	private static void SaveEdit(
		TestHost host,
		string target,
		string oldText,
		string newText) =>
		host.SessionEvent(
			host.SelectedSession,
			"sources",
			"saveEdit",
			new { target, oldText, newText });

	private static JsonElement? SourceEvent(TestHost host, string name) =>
		host.Bridge.LastEvent(host.SelectedSession.Address, "sources", name);

	// The last toast at a given level, or null until one arrives — the selector the notify-waiting tests poll.
	private static JsonElement? Notify(TestHost host, string level) =>
		host.Bridge.LastEvent(host.SelectedSession.Address, "notifications", "show") is { } n
			&& n.GetProperty("level").GetString() == level
				? n
				: null;

}
