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
[Collection("host-integration")]
public sealed class HostCoreSourcesTests {
	private static string Msg(object value) => JsonSerializer.Serialize(value);

	[Fact]
	public async Task OpenTarget_NonSourceUrl_RepliesOpenWeb() {
		await using var host = await TestHost.StartAsync();

		host.Send(Msg(new { type = "open-target", url = "https://example.com/page" }));

		var web = await Wait.ForAsync(() => host.Bridge.LastOfType("open-web"));
		Assert.Equal("https://example.com/page", web.GetProperty("url").GetString());
		Assert.Null(host.Bridge.LastOfType("source-doc")); // not a source — never fetched
	}

	[Fact]
	public async Task ConnectNotion_OpensTheTokenPageAndPromptsForTheToken() {
		await using var host = await TestHost.StartAsync();

		host.Send(Msg(new { type = "connect-notion" }));

		var prompt = await Wait.ForAsync(() => host.Bridge.LastOfType("prompt-source-token"));
		Assert.Equal("notion", prompt.GetProperty("sourceId").GetString());
		Assert.Equal("https://app.notion.com/developers/tokens", host.Platform.LastOpenedUrl);
	}

	[Fact]
	public async Task SetSourceToken_ValidatesAndSavesTheToken() {
		await using var host = await TestHost.StartAsync();
		host.SourceHttp.Responder = _ => (HttpStatusCode.OK, """{ "bot": { "workspace_name": "Acme" } }""");

		host.Send(Msg(new { type = "set-source-token", id = "r1", sourceId = "notion", token = "ntn_secret" }));

		var toast = await Wait.ForAsync(() => Notify(host, "info"));
		Assert.Contains("Acme", toast.GetProperty("message").GetString());
		// The dialog gets an ok result (so it closes), and the validated token was persisted to the source's file.
		var result = await Wait.ForAsync(() => host.Bridge.LastOfType("source-token-result"));
		Assert.True(result.GetProperty("ok").GetBoolean());
		string tokenFile = Path.Combine(host.SourcesDir, "notion.json");
		Assert.True(File.Exists(tokenFile));
		Assert.Contains("ntn_secret", File.ReadAllText(tokenFile));
		Assert.Contains(host.SourceHttp.Requests, r => r.RequestUri!.AbsoluteUri.Contains("/v1/users/me")
			&& r.Headers.Authorization is { Scheme: "Bearer", Parameter: "ntn_secret" });
	}

	[Fact]
	public async Task SetSourceToken_RejectedToken_RepliesInlineErrorAndDoesNotSave() {
		await using var host = await TestHost.StartAsync();
		host.SourceHttp.Responder = _ => (HttpStatusCode.Unauthorized, "{}");

		host.Send(Msg(new { type = "set-source-token", id = "r1", sourceId = "notion", token = "bad" }));

		// The rejection comes back as an inline result (not a toast), so the dialog stays open for a correction.
		var result = await Wait.ForAsync(() => host.Bridge.LastOfType("source-token-result"));
		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Contains("rejected", result.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
		Assert.False(File.Exists(Path.Combine(host.SourcesDir, "notion.json"))); // an invalid token is never saved
	}

	[Fact]
	public async Task SetSourceToken_ValidateFaults_StillRepliesSoTheDialogNeverHangs() {
		await using var host = await TestHost.StartAsync();
		// HttpClient's own request timeout surfaces as TaskCanceledException — the dialog must still get a result.
		host.SourceHttp.Responder = _ => throw new TaskCanceledException();

		host.Send(Msg(new { type = "set-source-token", id = "r1", sourceId = "notion", token = "ntn_x" }));

		var result = await Wait.ForAsync(() => host.Bridge.LastOfType("source-token-result"));
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

		host.Send(Msg(new { type = "open-target", url = "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d" }));

		var doc = await Wait.ForAsync(() => host.Bridge.LastOfType("source-doc"));
		Assert.Equal("https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", doc.GetProperty("target").GetString());
		Assert.Equal("Spec", doc.GetProperty("title").GetString());
		Assert.Equal("Body **text**", doc.GetProperty("markdown").GetString()); // the single render + Claude channel
		Assert.Equal("2026-06-30T06:15:48.000Z", doc.GetProperty("editedTime").GetString()); // read from the page JSON
		Assert.Equal("notion", doc.GetProperty("sourceId").GetString()); // keys the tab icon web-side
		var loading = host.Bridge.LastOfType("source-loading")!.Value; // posted first, so the tab opens with a spinner during the fetch, titled from the slug
		Assert.Equal("Spec", loading.GetProperty("title").GetString());
	}

	[Fact]
	public async Task OpenTarget_NotionUrlWithoutToken_RoutesToTheConnectPrompt() {
		await using var host = await TestHost.StartAsync();

		host.Send(Msg(new { type = "open-target", url = "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d" }));

		// Not connected: instead of a blank tab or an easy-to-miss error toast, the user is sent to connect.
		var prompt = await Wait.ForAsync(() => host.Bridge.LastOfType("prompt-source-token"));
		Assert.Equal("notion", prompt.GetProperty("sourceId").GetString());
		Assert.Equal("https://app.notion.com/developers/tokens", host.Platform.LastOpenedUrl);
		Assert.Null(host.Bridge.LastOfType("source-doc")); // nothing fetched without a token
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
		host.Send(Msg(new { type = "open-target", url = "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d" }));
		await Wait.ForAsync(() => host.Bridge.LastOfType("prompt-source-token"));
		host.Send(Msg(new { type = "set-source-token", id = "r1", sourceId = "notion", token = "ntn_secret" }));

		var doc = await Wait.ForAsync(() => host.Bridge.LastOfType("source-doc"));
		Assert.Equal("https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", doc.GetProperty("target").GetString());
		Assert.Equal("Spec", doc.GetProperty("title").GetString());
	}

	[Fact]
	public async Task SourceFetch_Failure_PostsSourceErrorIntoTheTab() {
		await using var host = await TestHost.StartAsync();
		WriteToken(host, "ntn_secret");
		host.SourceHttp.Responder = _ => (HttpStatusCode.InternalServerError, "{}");

		host.Send(Msg(new { type = "open-target", url = "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d" }));

		// The failure surfaces in the already-open tab (source-error keyed by target), not as a toast.
		var error = await Wait.ForAsync(() => host.Bridge.LastOfType("source-error"));
		Assert.Equal("https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", error.GetProperty("target").GetString());
		Assert.NotEmpty(error.GetProperty("message").GetString()!);
		Assert.Null(host.Bridge.LastOfType("source-doc"));
	}

	[Fact]
	public async Task SourceFetch_NonJsonOkBody_PostsSourceError_NotAStuckSpinner() {
		await using var host = await TestHost.StartAsync();
		WriteToken(host, "ntn_secret");
		// A 200 carrying non-JSON (proxy / captive-portal / incident HTML) throws JsonException deep in the parse;
		// the eager spinner is already up, so it must still resolve to an error rather than spin forever.
		host.SourceHttp.Responder = _ => (HttpStatusCode.OK, "<html>not json</html>");

		host.Send(Msg(new { type = "open-target", url = "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d" }));

		var error = await Wait.ForAsync(() => host.Bridge.LastOfType("source-error"));
		Assert.Equal("https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d", error.GetProperty("target").GetString());
		Assert.Null(host.Bridge.LastOfType("source-doc"));
	}

	private static void WriteToken(TestHost host, string token) {
		Directory.CreateDirectory(host.SourcesDir);
		File.WriteAllText(Path.Combine(host.SourcesDir, "notion.json"), Msg(new { token }));
	}

	// The last toast at a given level, or null until one arrives — the selector the notify-waiting tests poll.
	private static JsonElement? Notify(TestHost host, string level) =>
		host.Bridge.LastOfType("notify") is { } n && n.GetProperty("level").GetString() == level ? n : null;

}
