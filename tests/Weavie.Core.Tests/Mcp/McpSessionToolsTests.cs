using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Weavie.Core.Agents;
using Weavie.Core.Configuration;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;
using Weavie.Core.Sessions;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>currentSession / listSessions / readSession / messageSession on the registry server.</summary>
[Collection("Settings")]
public sealed class McpSessionToolsTests : IAsyncLifetime {
	private const string Token = "abcdef0123456789abcdef0123456789";
	private readonly TempDirectory _dir = new("weavie-session-tools-tests");
	private readonly SettingsStore _settings;
	private readonly FakePeers _peers = new(null, null);
	private readonly McpServer _registry;
	private readonly McpServer _ide;
	private readonly HttpClient _client = new();

	public McpSessionToolsTests() {
		_settings = CoreSettings.CreateStore(_dir.Combine("settings.toml"), enableWatcher: false);
		_registry = TestMcp.Server(Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path], _settings, sessions: _peers);
		_registry.Start();
		_ide = TestMcp.Ide(Token, FakeDiffPresenter.AlwaysKeep(), [_dir.Path]);
		_ide.Start();
	}

	public Task InitializeAsync() => Task.CompletedTask;

	public async Task DisposeAsync() {
		_client.Dispose();
		await _registry.DisposeAsync();
		await _ide.DisposeAsync();
		_settings.Dispose();
		_dir.Dispose();
	}

	[Fact]
	public async Task OnlyTheRegistryAdvertisesTheSessionTools() {
		string registry = await ToolsAsync(_registry);
		string ide = await ToolsAsync(_ide);
		foreach (string name in new[] { "currentSession", "listSessions", "readSession", "messageSession" }) {
			Assert.Contains($"\"{name}\"", registry, StringComparison.Ordinal);
			Assert.DoesNotContain($"\"{name}\"", ide, StringComparison.Ordinal);
		}
	}

	[Fact]
	public async Task CurrentSessionReturnsTheBoundId() {
		using var response = await CallAsync(_registry, "currentSession", "{}");

		Assert.Equal("""{"id":"feat/self"}""", Text(response));
	}

	[Fact]
	public async Task ASideGrantCallsAsItsOwnSession() {
		await using var grant = _registry.CreateSideGrant("b7");
		string send = Call("messageSession", """{"id":"feat/self","message":"finding"}""");

		using var current = await PostAsync(_registry, Call("currentSession", "{}"), grant.Credential.Token);
		using var sent = await PostAsync(_registry, send, grant.Credential.Token);
		using var main = await PostAsync(_registry, send, Token);

		Assert.Equal("""{"id":"feat/self#b7","parent":"feat/self"}""", Text(current));
		Assert.Equal([("feat/self#b7", "feat/self", "finding"), ("feat/self", "feat/self", "finding")], _peers.Sent);
	}

	[Fact]
	public async Task ListSessionsReportsSelfAndReceiveState() {
		using var response = await CallAsync(_registry, "listSessions", "{}");

		using var payload = JsonDocument.Parse(Text(response));
		Assert.Equal("feat/self", payload.RootElement.GetProperty("self").GetString());
		var peers = payload.RootElement.GetProperty("sessions").EnumerateArray()
			.Select(peer => (peer.GetProperty("id").GetString(), peer.GetProperty("receive").GetString())).ToList();
		Assert.Equal([("feat/self", "ready"), ("feat/other", "needsLoad"), ("feat/claude", "unsupported")], peers);
	}

	[Fact]
	public async Task ListSessionsIncludesSideSessionsOnRequest() {
		using var response = await CallAsync(_registry, "listSessions", """{"includeBtw":true}""");

		using var payload = JsonDocument.Parse(Text(response));
		var side = payload.RootElement.GetProperty("sessions").EnumerateArray().Last();
		Assert.Equal(
			("feat/self#b7", "feat/self", "why?"),
			(side.GetProperty("id").GetString(), side.GetProperty("parent").GetString(), side.GetProperty("prompt").GetString()));
		Assert.False(payload.RootElement.GetProperty("sessions")[0].TryGetProperty("parent", out _));
	}

	[Fact]
	public async Task ReadSessionReturnsTheTailAndTotal() {
		using var response = await CallAsync(_registry, "readSession", """{"id":"feat/other","count":2}""");

		Assert.False(IsError(response));
		Assert.Equal(("feat/other", 2), _peers.Read);
		using var payload = JsonDocument.Parse(Text(response));
		Assert.Equal(3, payload.RootElement.GetProperty("total").GetInt32());
		var entries = payload.RootElement.GetProperty("entries").EnumerateArray()
			.Select(entry => (entry.GetProperty("role").GetString(), entry.GetProperty("text").GetString())).ToList();
		Assert.Equal([("tool", "grep"), ("user", "ping"), ("agent", "done")], entries);
		Assert.Equal("feat/self", payload.RootElement.GetProperty("entries")[1].GetProperty("from").GetString());
		Assert.False(payload.RootElement.GetProperty("entries")[0].TryGetProperty("from", out _));
	}

	[Theory]
	[InlineData("""{"id":"feat/other"}""")]
	[InlineData("""{"id":"feat/other","count":0}""")]
	[InlineData("""{"id":"feat/other","count":"2"}""")]
	[InlineData("""{"count":2}""")]
	public async Task ReadSessionRequiresAnIdAndPositiveCount(string arguments) {
		using var response = await CallAsync(_registry, "readSession", arguments);

		Assert.True(IsError(response));
		Assert.Contains("positive integer 'count'", Text(response), StringComparison.Ordinal);
		Assert.Null(_peers.Read);
	}

	[Fact]
	public async Task MessageSessionDeliversAndReportsStatus() {
		using var response = await CallAsync(_registry, "messageSession", """{"id":"feat/other","message":"ping"}""");

		Assert.False(IsError(response));
		Assert.Equal([("feat/self", "feat/other", "ping")], _peers.Sent);
		Assert.Contains("status: working", Text(response), StringComparison.Ordinal);
	}

	[Fact]
	public async Task RefusalsAreToolErrors() {
		_peers.Refusal = "Session 'feat/other' isn't loaded.";

		using var response = await CallAsync(_registry, "messageSession", """{"id":"feat/other","message":"ping"}""");

		Assert.True(IsError(response));
		Assert.Equal(_peers.Refusal, Text(response));
	}

	private async Task<string> ToolsAsync(McpServer server) {
		using var response = await PostAsync(server, """{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", Token);
		return response.RootElement.GetRawText();
	}

	private Task<JsonDocument> CallAsync(McpServer server, string tool, string arguments) => PostAsync(server, Call(tool, arguments), Token);

	private static string Call(string tool, string arguments) =>
		$$$"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"{{{tool}}}","arguments":{{{arguments}}}}}""";

	private async Task<JsonDocument> PostAsync(McpServer server, string json, string token) {
		using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/mcp") {
			Content = new StringContent(json, Encoding.UTF8, "application/json"),
		};
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
		using var response = await _client.SendAsync(request);
		return await TestMcp.ReadResponseAsync(response);
	}

	private static bool IsError(JsonDocument response) =>
		response.RootElement.GetProperty("result").TryGetProperty("isError", out var error) && error.GetBoolean();

	private static string Text(JsonDocument response) =>
		response.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

	private sealed class FakePeers(FakePeers? root, string? side) : ISessionPeers {
		private FakePeers Root => root ?? this;

		public (string Id, int Count)? Read { get; private set; }

		public List<(string From, string Id, string Message)> Sent { get; } = [];

		public string? Refusal { get; set; }

		public string SelfId => side is null ? "feat/self" : $"feat/self#{side}";

		public string? ParentId => side is null ? null : "feat/self";

		public ISessionPeers Side(string conversationId) => new FakePeers(Root, conversationId);

		public Task<IReadOnlyList<SessionPeer>> ListAsync(bool includeSides, CancellationToken ct) => Task.FromResult<IReadOnlyList<SessionPeer>>([
			new("feat/self", "Self", "codex", true, "working", SessionPeerReceive.Ready, null, null),
			new("feat/other", "Other", "codex", false, "idle", SessionPeerReceive.NeedsLoad, null, null),
			new("feat/claude", "Claude", "claude", true, "idle", SessionPeerReceive.Unsupported, null, null),
			.. includeSides ? [new SessionPeer("feat/self#b7", "Self BTW", "codex", true, "idle", SessionPeerReceive.Ready, "feat/self", "why?")] : Array.Empty<SessionPeer>(),
		]);

		public Task<SessionPeerTranscript> ReadAsync(string sessionId, int count, CancellationToken ct) {
			Read = (sessionId, count);
			return Task.FromResult(new SessionPeerTranscript(sessionId, "Other", 3, [
				new(AgentTranscriptRole.Tool, "grep", "completed", null),
				new(AgentTranscriptRole.User, "ping", null, "feat/self"),
				new(AgentTranscriptRole.Agent, "done", null, null),
			]));
		}

		public Task<SessionPeerDelivery> SendAsync(string sessionId, string message, CancellationToken ct) {
			if (Root.Refusal is { } refusal) throw new SessionPeerException(refusal);
			Root.Sent.Add((SelfId, sessionId, message));
			return Task.FromResult(new SessionPeerDelivery(sessionId, "working"));
		}
	}
}
