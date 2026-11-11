using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
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
public sealed partial class McpAgentToolsTests : IAsyncLifetime {
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

	public Task InitializeAsync() => Task.CompletedTask;

	public async Task DisposeAsync() {
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
		var started = BlockConsult();
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
	public async Task HttpReplyStartsBeforeALongCallFinishes() {
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_codex.Consult = async (_, ct) => {
			await release.Task.WaitAsync(ct);
			return new AgentConsultSuccess { ModelId = "astra", Controls = [], Reply = "Looks right.", Denied = [] };
		};
		using var client = new HttpClient();

		using var response = await SendHeadersAsync(client, Request(2, "tools/call", """{"name":"consultAgent","arguments":{"provider":"codex","prompt":"x"}}"""));
		Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
		release.SetResult();

		using var reply = await TestMcp.ReadResponseAsync(response).WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Contains("Looks right.", Text(reply), StringComparison.Ordinal);
	}

	[Fact]
	public async Task HttpCallsRunIndependentlyWhileOneStreams() {
		var started = BlockConsult();
		using var client = new HttpClient();
		using var initialized = await PostAsync(client, Request(1, "initialize", "{}"), null);
		string session = initialized.Headers.GetValues("Mcp-Session-Id").Single();
		var call = PostAsync(client, Request(2, "tools/call", """{"name":"consultAgent","arguments":{"provider":"codex","prompt":"x"}}"""), session);
		var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		using var listed = await PostAsync(client, Request(3, "tools/call", """{"name":"listAgents","arguments":{}}"""), session).WaitAsync(TimeSpan.FromSeconds(5));
		using var reply = await TestMcp.ReadResponseAsync(listed);
		Assert.Contains("codex", Text(reply), StringComparison.Ordinal);
		using (await PostAsync(client, """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2}}""", "another-session")) { }
		Assert.False(token.IsCancellationRequested);

		using (await PostAsync(client, """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":2}}""", session)) { }
		await WaitCancelledAsync(token);
		using var cancelled = await call.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(string.Empty, await cancelled.Content.ReadAsStringAsync());
	}

	[Theory]
	[InlineData("\"t1\"")]
	[InlineData("7")]
	public async Task HttpLongCallReportsProgressUntilItsResult(string token) {
		_server.ProgressInterval = TimeSpan.FromMilliseconds(10);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_codex.Consult = async (_, ct) => {
			await release.Task.WaitAsync(ct);
			return new AgentConsultSuccess { ModelId = "astra", Controls = [], Reply = "Looks right.", Denied = [] };
		};
		using var client = new HttpClient();

		using var response = await SendHeadersAsync(client, ConsultWithToken(token));
		using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
		for (int expected = 1; expected <= 3; expected++) {
			using var progress = await ReadEventAsync(reader);
			Assert.Equal("notifications/progress", progress.RootElement.GetProperty("method").GetString());
			var parameters = progress.RootElement.GetProperty("params");
			Assert.Equal(token, parameters.GetProperty("progressToken").GetRawText());
			Assert.Equal(expected, parameters.GetProperty("progress").GetInt32());
		}
		release.SetResult();

		JsonDocument? reply = null;
		while (reply is null) {
			var message = await ReadEventAsync(reader);
			if (message.RootElement.TryGetProperty("id", out _)) reply = message; else message.Dispose();
		}
		using (reply) Assert.Contains("Looks right.", Text(reply), StringComparison.Ordinal);
		Assert.Empty((await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5))).Trim());
	}

	[Fact]
	public async Task HttpCallsWithoutATokenReportNoProgress() {
		_server.ProgressInterval = TimeSpan.FromMilliseconds(10);
		_codex.Consult = async (_, ct) => {
			await Task.Delay(100, ct);
			return new AgentConsultSuccess { ModelId = "astra", Controls = [], Reply = "Looks right.", Denied = [] };
		};
		using var client = new HttpClient();

		using var call = await PostAsync(client, Request(2, "tools/call", """{"name":"consultAgent","arguments":{"provider":"codex","prompt":"x"}}"""), null);

		Assert.DoesNotContain("notifications/progress", await call.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task WebSocketCallsReportNoProgress() {
		_server.ProgressInterval = TimeSpan.FromMilliseconds(10);
		_codex.Consult = async (_, ct) => {
			await Task.Delay(100, ct);
			return new AgentConsultSuccess { ModelId = "astra", Controls = [], Reply = "Looks right.", Denied = [] };
		};
		using var ws = await ConnectAsync();

		await SendAsync(ws, ConsultWithToken("\"t1\""));
		using var reply = await ReceiveAsync(ws);

		Assert.Contains("Looks right.", Text(reply), StringComparison.Ordinal);
	}

	[Fact]
	public async Task HttpToolThatStopsItselfStillAnswers() {
		_codex.Consult = (_, _) => Task.FromException<AgentConsultOutcome>(new OperationCanceledException());
		using var client = new HttpClient();

		using var response = await PostAsync(client, Request(2, "tools/call", """{"name":"consultAgent","arguments":{"provider":"codex","prompt":"x"}}"""), null);
		using var reply = await TestMcp.ReadResponseAsync(response);

		Assert.Equal(-32603, reply.RootElement.GetProperty("error").GetProperty("code").GetInt32());
	}

	[Fact]
	public async Task HttpConnectionResetStopsItsCall() {
		var started = BlockConsult();
		using var socket = new TcpClient();
		await socket.ConnectAsync(IPAddress.Loopback, _server.Port);
		string body = Request(2, "tools/call", """{"name":"consultAgent","arguments":{"provider":"codex","prompt":"x"}}""");
		await socket.GetStream().WriteAsync(Encoding.ASCII.GetBytes(
			$"POST /mcp HTTP/1.1\r\nHost: x\r\nAuthorization: Bearer {Token}\r\nContent-Length: {body.Length}\r\n\r\n{body}"));
		var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

		socket.LingerState = new LingerOption(true, 0);
		socket.Close();

		await WaitCancelledAsync(token);
	}

	[Fact]
	public async Task HttpClientHangingUpStopsItsCall() {
		var started = BlockConsult();
		using var client = new HttpClient();
		using var abort = new CancellationTokenSource();

		var call = client.SendAsync(Post(Request(2, "tools/call", """{"name":"consultAgent","arguments":{"provider":"codex","prompt":"x"}}"""), null), abort.Token);
		var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await abort.CancelAsync();

		await WaitCancelledAsync(token);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
	}

	private Task<HttpResponseMessage> SendHeadersAsync(HttpClient client, string json) =>
		client.SendAsync(Post(json, null), HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(5));

	private static string ConsultWithToken(string token) => Request(
		2, "tools/call", """{"name":"consultAgent","arguments":{"provider":"codex","prompt":"x"},"_meta":{"progressToken":""" + token + "}}");

	private static async Task<JsonDocument> ReadEventAsync(StreamReader reader) {
		while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is { } line) {
			if (line.StartsWith("data: ", StringComparison.Ordinal)) return JsonDocument.Parse(line[6..]);
		}
		throw new InvalidOperationException("The stream ended without an event.");
	}

	// A consult that runs until cancelled; the result completes with its token once it starts.
	private TaskCompletionSource<CancellationToken> BlockConsult() {
		var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
		_codex.Consult = async (_, ct) => {
			started.TrySetResult(ct);
			await Task.Delay(Timeout.Infinite, ct);
			throw new InvalidOperationException("unreachable");
		};
		return started;
	}

	private static async Task WaitCancelledAsync(CancellationToken token) {
		var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var registration = token.Register(() => cancelled.TrySetResult());
		await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
	}

	private Task<ClientWebSocket> ConnectAsync() => ConnectAsync(Token);

	private async Task<ClientWebSocket> ConnectAsync(string token) {
		var client = new ClientWebSocket();
		client.Options.SetRequestHeader("Authorization", $"Bearer {token}");
		await client.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/"), CancellationToken.None);
		return client;
	}

	private static async Task<JsonDocument> CallAsync(ClientWebSocket ws, int id, string tool, string arguments) {
		await SendAsync(ws, Request(id, "tools/call", $$"""{"name":"{{tool}}","arguments":{{arguments}}}"""));
		return await ReceiveAsync(ws);
	}

	private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket ws) {
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

	private HttpRequestMessage Post(string json, string? session) => Post(json, session, Token);

	private HttpRequestMessage Post(string json, string? session, string token) {
		var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{_server.Port}/mcp") {
			Content = new StringContent(json, Encoding.UTF8, "application/json"),
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		if (session is not null) request.Headers.Add("Mcp-Session-Id", session);
		return request;
	}

	private static string Text(JsonDocument response) =>
		response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

	private static string Request(int id, string method, string parameters) =>
		$"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{parameters}}}";
}
