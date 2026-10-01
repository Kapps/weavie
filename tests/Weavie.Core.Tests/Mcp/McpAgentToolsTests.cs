using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.Mcp;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>listAgents/consultAgent on the registry server, and per-call cancellation of long tool calls.</summary>
[Collection("Settings")]
public sealed class McpAgentToolsTests : IAsyncDisposable {
	private const string Token = "abcdef0123456789abcdef0123456789";
	private readonly TempDirectory _dir = new("weavie-agent-tools-tests");
	private readonly SettingsStore _settings;
	private readonly FakeConsultProvider _codex = new("codex");
	private readonly AgentModelCatalog _models;
	private readonly McpServer _server;

	public McpAgentToolsTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		var providers = new AgentProviderRegistry();
		providers.Register(_codex);
		_models = new AgentModelCatalog(providers);
		_server = TestMcp.Server(
			Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], "weavie", _settings, registryMode: true,
			agents: new AgentConsultation(providers, _models));
		_server.Start();
	}

	public async ValueTask DisposeAsync() {
		await _server.DisposeAsync();
		_models.Dispose();
		_settings.Dispose();
		_dir.Dispose();
	}

	[Fact]
	public async Task ListAgentsReportsEachProvidersModels() {
		_models.Start();
		_models.Observe("codex", [FakeConsultProvider.Models("gpt", "astra")], AgentModelSource.Session);
		using var ws = await ConnectAsync();

		using var response = await CallAsync(ws, 1, "listAgents", "{}");

		using var roster = JsonDocument.Parse(Text(response));
		var codex = roster.RootElement.GetProperty("agents").EnumerateArray().Single();
		Assert.Equal("codex", codex.GetProperty("provider").GetString());
		Assert.True(codex.GetProperty("consultable").GetBoolean());
		var models = codex.GetProperty("models");
		Assert.Equal("ready", models.GetProperty("state").GetString());
		Assert.Equal("session", models.GetProperty("source").GetString());
		Assert.Equal(["gpt", "astra"], models.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetString()));
	}

	[Fact]
	public async Task ConsultAgentRunsInTheSessionWorktreeAndReturnsTheReply() {
		AgentConsultRequest? seen = null;
		_codex.Consult = (request, _) => {
			seen = request;
			return Task.FromResult<AgentConsultOutcome>(new AgentConsultSuccess {
				ModelId = "astra",
				Controls = [],
				Reply = "Looks right.",
				Denied = ["Run the tests"],
			});
		};
		using var ws = await ConnectAsync();

		using var response = await CallAsync(ws, 1, "consultAgent", """{"provider":"codex","model":"astra","prompt":"Review it."}""");

		Assert.Equal("codex (astra) replied:\n\nLooks right.\n\n(Weavie denied: Run the tests)", Text(response));
		Assert.Equal(new AgentConsultRequest { Workspace = _dir.Path, Model = "astra", Prompt = "Review it." }, seen);
	}

	[Fact]
	public async Task ConsultFailuresAreToolErrors() {
		using var ws = await ConnectAsync();

		using var response = await CallAsync(ws, 1, "consultAgent", """{"provider":"gemini","prompt":"Review it."}""");

		Assert.True(response.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
		Assert.Contains("listAgents", Text(response), StringComparison.Ordinal);
	}

	[Fact]
	public async Task CancelledNotificationStopsOnlyThatClientsCall() {
		var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		_codex.Consult = async (_, ct) => {
			started.TrySetResult(ct);
			await Task.Delay(Timeout.Infinite, ct);
			throw new InvalidOperationException("unreachable");
		};
		using var caller = await ConnectAsync();
		using var other = await ConnectAsync();
		await SendAsync(caller, Request(7, "tools/call", """{"name":"consultAgent","arguments":{"provider":"codex","prompt":"x"}}"""));
		var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		await SendAsync(other, """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""");
		using (await CallAsync(other, 1, "listAgents", "{}")) { }
		Assert.False(token.IsCancellationRequested);

		await SendAsync(caller, """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""");
		await WaitCancelledAsync(token);
	}

	[Fact]
	public async Task HttpClientsGetASessionThatScopesCancellation() {
		var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		_codex.Consult = async (_, ct) => {
			started.TrySetResult(ct);
			await Task.Delay(Timeout.Infinite, ct);
			throw new InvalidOperationException("unreachable");
		};
		using var client = new HttpClient();
		using var initialized = await PostAsync(client, Request(1, "initialize", "{}"), null);
		string session = initialized.Headers.GetValues("Mcp-Session-Id").Single();

		var call = PostAsync(client, Request(2, "tools/call", """{"name":"consultAgent","arguments":{"provider":"codex","prompt":"x"}}"""), session);
		var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		using (await PostAsync(client, """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2}}""", session)) { }

		await WaitCancelledAsync(token);
		using var cancelled = await call;
		Assert.Equal(System.Net.HttpStatusCode.Accepted, cancelled.StatusCode);
	}

	[Fact]
	public async Task HttpClientHangingUpStopsItsCall() {
		var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		_codex.Consult = async (_, ct) => {
			started.TrySetResult(ct);
			await Task.Delay(Timeout.Infinite, ct);
			throw new InvalidOperationException("unreachable");
		};
		using var client = new HttpClient();
		using var abort = new CancellationTokenSource();

		var call = client.SendAsync(Post(Request(2, "tools/call", """{"name":"consultAgent","arguments":{"provider":"codex","prompt":"x"}}"""), null), abort.Token);
		var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await abort.CancelAsync();

		await WaitCancelledAsync(token);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
	}

	private static async Task WaitCancelledAsync(CancellationToken token) {
		var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = token.Register(() => cancelled.TrySetResult());
		await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
	}

	private async Task<ClientWebSocket> ConnectAsync() {
		var client = new ClientWebSocket();
		client.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
		await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/"), CancellationToken.None);
		return client;
	}

	private static async Task<JsonDocument> CallAsync(ClientWebSocket ws, int id, string tool, string arguments) {
		await SendAsync(ws, Request(id, "tools/call", $$"""{"name":"{{tool}}","arguments":{{arguments}}}"""));
		byte[] buffer = new byte[64 * 1024];
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
		var message = new MemoryStream();
		WebSocketReceiveResult result;
		do {
			result = await ws.ReceiveAsync(buffer, timeout.Token);
			message.Write(buffer, 0, result.Count);
		} while (!result.EndOfMessage);
		return JsonDocument.Parse(message.ToArray());
	}

	private static Task SendAsync(ClientWebSocket ws, string json) =>
		ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

	private Task<HttpResponseMessage> PostAsync(HttpClient client, string json, string? session) =>
		client.SendAsync(Post(json, session));

	private HttpRequestMessage Post(string json, string? session) {
		var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_server.Port}/mcp") {
			Content = new StringContent(json, Encoding.UTF8, "application/json"),
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
		if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
		return request;
	}

	private static string Text(JsonDocument response) =>
		response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

	private static string Request(int id, string method, string parameters) =>
		$"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{parameters}}}";
}
