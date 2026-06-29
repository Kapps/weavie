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
	public async Task ConnectNotion_OpensTheTokenPageAndPromptsForTheToken() {
		await using var host = await TestHost.StartAsync();

		host.Send(Msg(new { type = "connect-notion" }));

		var prompt = await WaitForAsync(() => host.Bridge.LastOfType("prompt-source-token"));
		Assert.Equal("notion", prompt.GetProperty("sourceId").GetString());
		Assert.Equal("https://app.notion.com/developers/tokens", host.Platform.LastOpenedUrl);
	}

	[Fact]
	public async Task SetSourceToken_ValidatesAndSavesTheToken() {
		await using var host = await TestHost.StartAsync();
		host.SourceHttp.Responder = _ => (HttpStatusCode.OK, """{ "bot": { "workspace_name": "Acme" } }""");

		host.Send(Msg(new { type = "set-source-token", id = "r1", sourceId = "notion", token = "ntn_secret" }));

		var toast = await WaitForAsync(() => Notify(host, "info"));
		Assert.Contains("Acme", toast.GetProperty("message").GetString());
		// The dialog gets an ok result (so it closes), and the validated token was persisted to the source's file.
		var result = await WaitForAsync(() => host.Bridge.LastOfType("source-token-result"));
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
		var result = await WaitForAsync(() => host.Bridge.LastOfType("source-token-result"));
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

		var result = await WaitForAsync(() => host.Bridge.LastOfType("source-token-result"));
		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.False(File.Exists(Path.Combine(host.SourcesDir, "notion.json")));
	}

	[Fact]
	public async Task SourceFetch_AfterConnect_ReturnsTheMappedDoc() {
		await using var host = await TestHost.StartAsync();
		WriteToken(host, "ntn_secret");
		host.SourceHttp.Responder = request => request.RequestUri!.AbsoluteUri switch {
			var u when u.Contains("/pages/") => (HttpStatusCode.OK, """{ "properties": { "Name": { "type": "title", "title": [ { "plain_text": "Spec" } ] } } }"""),
			var u when u.Contains("/children") => (HttpStatusCode.OK, """{ "results": [ { "type": "paragraph", "paragraph": { "rich_text": [ { "plain_text": "Body text" } ] } } ] }"""),
			_ => (HttpStatusCode.NotFound, "{}"),
		};

		host.Send(Msg(new { type = "source-fetch", id = "s1", target = "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d" }));

		var doc = await WaitForAsync(() => host.Bridge.LastOfType("source-doc"));
		Assert.Equal("s1", doc.GetProperty("id").GetString());
		Assert.Equal("Spec", doc.GetProperty("title").GetString());
		Assert.Equal("Body text", doc.GetProperty("text").GetString());        // Claude's channel
		Assert.Contains("<p>Body text</p>", doc.GetProperty("html").GetString()); // the rendered surface
	}

	[Fact]
	public async Task SourceFetch_WithoutToken_Toasts() {
		await using var host = await TestHost.StartAsync();

		host.Send(Msg(new { type = "source-fetch", id = "s1", target = "https://www.notion.so/Spec-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d" }));

		var error = await WaitForAsync(() => Notify(host, "error"));
		Assert.Contains("Connect", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
	}

	private static void WriteToken(TestHost host, string token) {
		Directory.CreateDirectory(host.SourcesDir);
		File.WriteAllText(Path.Combine(host.SourcesDir, "notion.json"), Msg(new { token }));
	}

	// The last toast at a given level, or null until one arrives — the selector the notify-waiting tests poll.
	private static JsonElement? Notify(TestHost host, string level) =>
		host.Bridge.LastOfType("notify") is { } n && n.GetProperty("level").GetString() == level ? n : null;

	private static async Task<JsonElement> WaitForAsync(Func<JsonElement?> selector) {
		for (int i = 0; i < 200; i++) {
			if (selector() is { } value) {
				return value;
			}

			await Task.Delay(25);
		}

		throw new TimeoutException("Condition was not met within the timeout.");
	}
}
