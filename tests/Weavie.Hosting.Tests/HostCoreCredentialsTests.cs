using System.Net;
using System.Text.Json;
using Weavie.Core.Credentials;
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
	public async Task ConnectWithAService_OpensItsKeyPageAndPromptsForTheKey() {
		await using var host = await TestHost.StartAsync();

		var result = await host.InvokeClientCommandAsync("weavie.credentials.connect", new { service = "notion" });

		Assert.True(result.Ok, result.Error);
		var prompt = await Wait.ForAsync(() => Prompt(host));
		Assert.Equal("notion", prompt.GetProperty("id").GetString());
		Assert.Equal("Notion", prompt.GetProperty("displayName").GetString());
		Assert.Equal("personal access token", prompt.GetProperty("keyNoun").GetString());
		Assert.Equal("https://app.notion.com/developers/tokens", prompt.GetProperty("keyPageUrl").GetString());
		Assert.Equal("https://app.notion.com/developers/tokens", host.Platform.LastOpenedUrl);
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
	public async Task SavedKey_IsValidatedSavedAndAnnounced() {
		await using var host = await TestHost.StartAsync();
		host.Http.Responder = _ => (HttpStatusCode.OK, Acme);
		Assert.True((await host.InvokeClientCommandAsync("weavie.credentials.connect", new { service = "notion" })).Ok);

		var result = await SaveKey(host, " ntn_secret ");

		Assert.True(result.GetProperty("ok").GetBoolean());
		var toast = await Wait.ForAsync(() => host.Bridge.LastEvent(host.SelectedSession.Address, "notifications", "show"));
		Assert.Equal("Connected Notion (Acme).", toast.GetProperty("message").GetString());
		Assert.Equal("ntn_secret", File.ReadAllText(Path.Combine(host.CredentialsDir, "notion.key")));
		Assert.Contains(host.Http.Requests, r => r.RequestUri!.AbsoluteUri.Contains("/v1/users/me")
			&& r.Headers.Authorization is { Scheme: "Bearer", Parameter: "ntn_secret" });
		var hosts = await Wait.ForAsync(() => host.Bridge.LastEvent("sources", "linkHosts"));
		Assert.Contains("notion.so", hosts.GetProperty("hosts").EnumerateArray().Select(h => h.GetString()));
	}

	[Fact]
	public async Task RejectedKey_RepliesInlineAndIsNeverSaved() {
		await using var host = await TestHost.StartAsync();
		host.Http.Responder = _ => (HttpStatusCode.Unauthorized, "{}");

		var result = await SaveKey(host, "bad");

		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.Contains("rejected", result.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
		Assert.False(File.Exists(Path.Combine(host.CredentialsDir, "notion.key")));
	}

	[Fact]
	public async Task ValidationFault_StillRepliesSoTheDialogNeverHangs() {
		await using var host = await TestHost.StartAsync();
		// HttpClient's own request timeout surfaces as TaskCanceledException — the dialog must still get a result.
		host.Http.Responder = _ => throw new TaskCanceledException();

		var result = await SaveKey(host, "ntn_x");

		Assert.False(result.GetProperty("ok").GetBoolean());
		Assert.False(File.Exists(Path.Combine(host.CredentialsDir, "notion.key")));
	}

	[Fact]
	public async Task SuccessfulConnect_DoesNotReplayThePrompt() {
		await using var host = await TestHost.StartAsync();
		host.Http.Responder = _ => (HttpStatusCode.OK, Acme);
		Assert.True((await host.InvokeClientCommandAsync("weavie.credentials.connect", new { service = "notion" })).Ok);
		await Wait.ForAsync(() => Prompt(host));

		Assert.True((await SaveKey(host, "ntn_secret")).GetProperty("ok").GetBoolean());

		Assert.Null(await PromptAfterSync(host));
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

		Assert.True((await host.InvokeClientCommandAsync("weavie.credentials.connect", new { })).Ok);

		var picker = await Wait.ForAsync(() => Picker(host));
		var option = Assert.Single(picker.GetProperty("options").EnumerateArray());
		Assert.Equal("notion", option.GetProperty("id").GetString());
		Assert.Equal("Notion", option.GetProperty("label").GetString());
		Assert.Equal("Not connected", option.GetProperty("group").GetString());
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
	public async Task DisconnectWithAService_DeletesItsKey() {
		await using var host = await TestHost.StartAsync();
		Connect(host);

		var result = await host.InvokeClientCommandAsync("weavie.credentials.disconnect", new { service = "notion" });

		Assert.True(result.Ok, result.Error);
		Assert.False(File.Exists(Path.Combine(host.CredentialsDir, "notion.key")));
	}

	[Fact]
	public async Task DisconnectWithAService_DeletesAnUnreadableKey() {
		await using var host = await TestHost.StartAsync();
		Directory.CreateDirectory(host.CredentialsDir);
		File.WriteAllText(Path.Combine(host.CredentialsDir, "notion.key"), " ");

		Assert.True((await host.InvokeClientCommandAsync("weavie.credentials.disconnect", new { service = "notion" })).Ok);

		Assert.False(File.Exists(Path.Combine(host.CredentialsDir, "notion.key")));
	}

	[Fact]
	public async Task DisconnectWithoutAService_PicksAmongConnectedServicesOnly() {
		await using var host = await TestHost.StartAsync();
		Assert.False((await host.InvokeClientCommandAsync("weavie.credentials.disconnect", new { })).Ok);
		Assert.Null(Picker(host));
		Connect(host);

		Assert.True((await host.InvokeClientCommandAsync("weavie.credentials.disconnect", new { })).Ok);

		var picker = await Wait.ForAsync(() => Picker(host));
		Assert.Equal("notion", Assert.Single(picker.GetProperty("options").EnumerateArray()).GetProperty("id").GetString());
		host.SessionEvent(host.SelectedSession, "picker", "pick", new { id = "notion" });
		await Wait.UntilAsync(() => !File.Exists(Path.Combine(host.CredentialsDir, "notion.key")));
	}

	internal static Task<JsonElement> SaveKey(TestHost host, string key) =>
		host.SessionRequestAsync<JsonElement>(host.SelectedSession, "credentials", "save", new { id = "notion", key });

	private static void Connect(TestHost host) =>
		new FileCredentialStore(host.CredentialsDir).Save("notion", new CredentialSecret("ntn_secret"));

	private static JsonElement? Prompt(TestHost host) =>
		host.Bridge.LastEvent(host.SelectedSession.Address, "credentials", "prompt");

	private static JsonElement? Picker(TestHost host) =>
		host.Bridge.LastEvent(host.SelectedSession.Address, "picker", "show");

	private static async Task<JsonElement?> PromptAfterSync(TestHost host) {
		host.Bridge.Clear();
		await host.SessionRequestAsync<JsonElement>(host.SelectedSession, "lifecycle", "sync", new { });
		return Prompt(host);
	}
}
