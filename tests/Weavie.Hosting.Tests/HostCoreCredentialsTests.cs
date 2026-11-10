using System.Net;
using System.Text.Json;
using Xunit;

namespace Weavie.Hosting.Tests;

/// <summary>
/// Connect Service… / Disconnect Service… over the real host: the prompt, the validated save against a stubbed
/// Notion <c>GET /v1/users/me</c>, the host-driven picker, and deleting a key — all without the network.
/// </summary>
[Collection(TestCollections.HostIntegration)]
public sealed class HostCoreCredentialsTests {
	private const string Acme = """{ "bot": { "workspace_name": "Acme" } }""";

	[Fact]
	public async Task ConnectWithAService_PromptsForTheKeyThenValidatesSavesAndAnnouncesIt() {
		await using var host = await TestHost.StartAsync();
		host.Http.Responder = _ => (HttpStatusCode.OK, Acme);

		var result = await host.InvokeClientCommandAsync("weavie.credentials.connect", new { service = "notion" });

		Assert.Equal((true, null), (result.Ok, result.Message));
		var prompt = await Wait.ForAsync(() => Prompt(host));
		Assert.Equal(("notion", "Notion", "personal access token", "https://app.notion.com/developers/tokens"), (
			prompt.GetProperty("id").GetString(), prompt.GetProperty("displayName").GetString(),
			prompt.GetProperty("keyNoun").GetString(), prompt.GetProperty("keyPageUrl").GetString()));
		Assert.Equal("https://app.notion.com/developers/tokens", host.Platform.LastOpenedUrl);

		Assert.True((await SaveKey(host, " ntn_secret ")).GetProperty("ok").GetBoolean());

		var toast = await Wait.ForAsync(() => host.Bridge.LastEvent(host.SelectedSession.Address, "notifications", "show"));
		Assert.Equal("Connected Notion (Acme).", toast.GetProperty("message").GetString());
		Assert.Equal("ntn_secret", File.ReadAllText(Path.Combine(host.CredentialsDir, "notion.key")));
		Assert.Contains(host.Http.Requests, r => r.RequestUri!.AbsoluteUri.Contains("/v1/users/me")
			&& r.Headers.Authorization is { Scheme: "Bearer", Parameter: "ntn_secret" });
		var hosts = await Wait.ForAsync(() => host.Bridge.LastEvent("sources", "linkHosts"));
		Assert.Contains("notion.so", hosts.GetProperty("hosts").EnumerateArray().Select(h => h.GetString()));
		Assert.Null(await PromptAfterSync(host));
	}

	[Fact]
	public async Task ConnectWithAnUndeclaredService_Fails() {
		await using var host = await TestHost.StartAsync();

		var result = await host.InvokeClientCommandAsync("weavie.credentials.connect", new { service = "dropbox" });

		Assert.False(result.Ok);
		Assert.Contains("notion", result.Error, StringComparison.Ordinal);
		Assert.Null(Prompt(host));
	}

	[Fact]
	public async Task RejectedKeyOrValidationFault_RepliesInlineAndIsNeverSaved() {
		await using var host = await TestHost.StartAsync();
		host.Http.Responder = _ => (HttpStatusCode.Unauthorized, "{}");

		var rejected = await SaveKey(host, "bad");
		// HttpClient's own request timeout surfaces as TaskCanceledException; the dialog must still get a result.
		host.Http.Responder = _ => throw new TaskCanceledException();
		var faulted = await SaveKey(host, "ntn_x");

		Assert.False(rejected.GetProperty("ok").GetBoolean());
		Assert.Contains("rejected", rejected.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
		Assert.False(faulted.GetProperty("ok").GetBoolean());
		Assert.False(File.Exists(Path.Combine(host.CredentialsDir, "notion.key")));
	}

	[Fact]
	public async Task DismissedPrompt_RemovesItsDurableState() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.InvokeClientCommandAsync("weavie.credentials.connect", new { service = "notion" })).Ok);
		await Wait.ForAsync(() => Prompt(host));

		host.SessionEvent(host.SelectedSession, "credentials", "dismiss", new { });

		Assert.Null(await PromptAfterSync(host));
	}

	[Fact]
	public async Task ConnectWithoutAService_PicksOneThenPrompts() {
		await using var host = await TestHost.StartAsync();

		var result = await host.InvokeClientCommandAsync("weavie.credentials.connect", new { });

		Assert.Equal((true, null), (result.Ok, result.Message));

		var picker = await Wait.ForAsync(() => Picker(host));
		var options = picker.GetProperty("options").EnumerateArray().ToList();
		Assert.Equal(["Notion", "OpenAI", "TypeSafe"], options.Select(option => option.GetProperty("label").GetString()));
		Assert.Equal(["notion", "openai", "typesafe"], options.Select(option => option.GetProperty("id").GetString()));
		Assert.All(options, option => Assert.Equal("Not connected", option.GetProperty("group").GetString()));
		Assert.Null(Prompt(host));

		host.SessionEvent(host.SelectedSession, "picker", "pick", new { id = "notion" });

		Assert.Equal("notion", (await Wait.ForAsync(() => Prompt(host))).GetProperty("id").GetString());
		host.Bridge.Clear();
		await host.SessionRequestAsync<JsonElement>(host.SelectedSession, "lifecycle", "sync", new { });
		Assert.Null(Picker(host));
	}

	[Fact]
	public async Task DismissedPicker_RunsNothing() {
		await using var host = await TestHost.StartAsync();
		Assert.True((await host.InvokeClientCommandAsync("weavie.credentials.connect", new { })).Ok);
		await Wait.ForAsync(() => Picker(host));

		host.SessionEvent(host.SelectedSession, "picker", "dismiss", new { });
		host.SessionEvent(host.SelectedSession, "picker", "pick", new { id = "notion" });
		host.DrainMessages();

		Assert.Null(Prompt(host));
	}

	[Fact]
	public async Task DisconnectWithAService_DeletesItsKeyEvenAnUnreadableOne() {
		await using var host = await TestHost.StartAsync();
		host.SaveCredential("notion", "ntn_secret");

		var result = await host.InvokeClientCommandAsync("weavie.credentials.disconnect", new { service = "notion" });

		Assert.Equal((true, "Disconnected Notion."), (result.Ok, result.Message));
		Assert.False(File.Exists(Path.Combine(host.CredentialsDir, "notion.key")));
		Assert.Equal("Notion isn't connected.",
			(await host.InvokeClientCommandAsync("weavie.credentials.disconnect", new { service = "notion" })).Message);
		File.WriteAllText(Path.Combine(host.CredentialsDir, "notion.key"), " ");
		Assert.Equal("Disconnected Notion.",
			(await host.InvokeClientCommandAsync("weavie.credentials.disconnect", new { service = "notion" })).Message);
	}

	[Fact]
	public async Task DisconnectWithoutAService_PicksAmongConnectedServicesOnly() {
		await using var host = await TestHost.StartAsync();
		Assert.False((await host.InvokeClientCommandAsync("weavie.credentials.disconnect", new { })).Ok);
		Assert.Null(Picker(host));
		host.SaveCredential("notion", "ntn_secret");

		var result = await host.InvokeClientCommandAsync("weavie.credentials.disconnect", new { });

		Assert.Equal((true, null), (result.Ok, result.Message));
		var picker = await Wait.ForAsync(() => Picker(host));
		Assert.Equal("notion", Assert.Single(picker.GetProperty("options").EnumerateArray()).GetProperty("id").GetString());
		host.SessionEvent(host.SelectedSession, "picker", "pick", new { id = "notion" });
		await Wait.UntilAsync(() => !File.Exists(Path.Combine(host.CredentialsDir, "notion.key")));
	}

	internal static Task<JsonElement> SaveKey(TestHost host, string key) => SaveKey(host, "notion", key);

	internal static Task<JsonElement> SaveKey(TestHost host, string id, string key) =>
		host.SessionRequestAsync<JsonElement>(host.SelectedSession, "credentials", "save", new { id, key });

	internal static JsonElement? Prompt(TestHost host) =>
		host.Bridge.LastEvent(host.SelectedSession.Address, "credentials", "prompt");

	internal static JsonElement? Picker(TestHost host) =>
		host.Bridge.LastEvent(host.SelectedSession.Address, "picker", "show");

	private static async Task<JsonElement?> PromptAfterSync(TestHost host) {
		host.Bridge.Clear();
		await host.SessionRequestAsync<JsonElement>(host.SelectedSession, "lifecycle", "sync", new { });
		return Prompt(host);
	}
}
