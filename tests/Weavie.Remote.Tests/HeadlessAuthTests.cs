using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Weavie.Hosting.Web;
using Xunit;

namespace Weavie.Remote.Tests;

/// <summary>Launches a real network-exposed (remote-mode) <c>Weavie.Headless</c> worker once for the suite.</summary>
public sealed class RemoteHeadlessFixture : IAsyncLifetime {
	private readonly TempDirectory _workspace = new("weavie-remote-tests");

	public HostHandle Host { get; private set; } = null!;

	public async Task InitializeAsync() {
		int port = Hosts.FreePort();
		Host = await HostHandle.StartAsync(
			Hosts.HeadlessDll,
			["--remote", "--bind", "127.0.0.1", "--port", port.ToString(), "--token", Tokens.Correct,
				"--workspace", _workspace.Path, "--spawn-contract", WorkspaceControlProtocol.SpawnContract.ToString()],
			port,
			readyMarker: "open  http://",
			timeout: TimeSpan.FromSeconds(60));
	}

	public async Task DisposeAsync() {
		await Host.DisposeAsync();
		_workspace.Dispose();
	}
}

/// <summary>
/// Black-box auth against a real remote-mode headless worker: the document establishes a cookie while bridge
/// transports retain explicit token auth for cross-origin aggregation.
/// </summary>
public sealed class HeadlessRemoteAuthTests(RemoteHeadlessFixture fixture) : IClassFixture<RemoteHeadlessFixture> {
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

	[Theory]
	[MemberData(nameof(Tokens.Denied), MemberType = typeof(Tokens))]
	public async Task Document_shows_the_connect_page_without_a_cookie(string variant) {
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/{Tokens.QuerySuffix(variant)}");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("Connect to Weavie", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Query_token_does_not_bypass_the_connect_page() {
		var response = await Http.GetAsync($"{fixture.Host.PageUrl}?token={Tokens.Correct}");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("Connect to Weavie", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
		Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
	}

	[Fact]
	public async Task Correct_token_sets_a_persistent_cookie_and_redirects_to_the_clean_page() {
		var cookies = new CookieContainer();
		using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies };
		using var client = new HttpClient(handler);
		var response = await client.PostAsync(
			fixture.Host.PageUrl,
			new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = Tokens.Correct }));

		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal("/index.html", response.Headers.Location?.OriginalString);
		Assert.Contains(response.Headers.GetValues("Set-Cookie"), value =>
			value.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase)
			&& value.Contains("SameSite=Strict", StringComparison.OrdinalIgnoreCase));
		var document = await client.GetAsync(fixture.Host.PageUrl);
		string html = await document.Content.ReadAsStringAsync();
		Assert.Equal(HttpStatusCode.OK, document.StatusCode);
		Assert.DoesNotContain("Connect to Weavie", html, StringComparison.Ordinal);
		Assert.Empty(new Uri(fixture.Host.PageUrl).Query);
	}

	[Theory]
	[MemberData(nameof(Tokens.Denied), MemberType = typeof(Tokens))]
	public async Task Wrong_connect_token_is_rejected(string variant) {
		string token = Tokens.Value(variant) ?? string.Empty;
		var response = await Http.PostAsync(
			fixture.Host.PageUrl,
			new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));

		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		Assert.Contains("not accepted", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Theory]
	[MemberData(nameof(Tokens.Denied), MemberType = typeof(Tokens))]
	public async Task Bridge_is_denied_without_a_valid_token(string variant) {
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/weavie-bridge{Tokens.QuerySuffix(variant)}");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Bridge_with_correct_token_passes_auth_then_rejects_non_websocket() {
		// Correct token clears the gate; the non-WebSocket GET reaches the bridge and gets 400 (not 401),
		// proving rejection is "not a WebSocket," not "unauthorized."
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/weavie-bridge?token={Tokens.Correct}");
		Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
	}

	[Fact]
	public async Task Bridge_websocket_upgrade_succeeds_with_correct_token() {
		using var socket = new ClientWebSocket();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{fixture.Host.Port}/weavie-bridge?token={Tokens.Correct}"), cts.Token);
		// The successful upgrade is the assertion. (Closing is best-effort: the host may drop the socket
		// without a full close handshake, which isn't an auth concern.)
		Assert.Equal(WebSocketState.Open, socket.State);
		try {
			await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
		} catch (WebSocketException) {
		}
	}

	[Fact]
	public async Task Bridge_websocket_upgrade_is_rejected_without_a_token() {
		using var socket = new ClientWebSocket();
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await Assert.ThrowsAsync<WebSocketException>(() =>
			socket.ConnectAsync(new Uri($"ws://127.0.0.1:{fixture.Host.Port}/weavie-bridge"), cts.Token));
	}

	[Fact]
	public async Task Bridge_websocket_upgrade_succeeds_with_a_foreign_origin_when_token_gated() {
		// In remote mode the token is the gate and the real client is cross-origin by design (the app at
		// https://weavie.dev, or the runner-hosted browser page on another port), so a foreign Origin + correct
		// token must connect. The same-origin (CSWSH) check applies to the local no-token mode only. Regression: the
		// hardening applied it unconditionally and 403'd every remote agent's bridge. See remote-sessions.md.
		using var socket = new ClientWebSocket();
		socket.Options.SetRequestHeader("Origin", "https://weavie.dev");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{fixture.Host.Port}/weavie-bridge?token={Tokens.Correct}"), cts.Token);
		Assert.Equal(WebSocketState.Open, socket.State);
		try {
			await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
		} catch (WebSocketException) {
		}
	}

	[Fact]
	public async Task Bridge_websocket_upgrade_succeeds_with_a_matching_origin() {
		using var socket = new ClientWebSocket();
		socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{fixture.Host.Port}");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{fixture.Host.Port}/weavie-bridge?token={Tokens.Correct}"), cts.Token);
		Assert.Equal(WebSocketState.Open, socket.State);
		try {
			await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
		} catch (WebSocketException) {
		}
	}

	[Fact]
	public async Task Cookie_authenticated_bridge_requires_the_matching_origin() {
		var cookies = new CookieContainer();
		using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies };
		using var client = new HttpClient(handler);
		var connect = await client.PostAsync(
			fixture.Host.PageUrl,
			new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = Tokens.Correct }));
		Assert.Equal(HttpStatusCode.Redirect, connect.StatusCode);

		using var accepted = new ClientWebSocket();
		accepted.Options.Cookies = cookies;
		accepted.Options.SetRequestHeader("Origin", fixture.Host.BaseUrl);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await accepted.ConnectAsync(
			new Uri($"ws://127.0.0.1:{fixture.Host.Port}/weavie-bridge"),
			cts.Token);
		Assert.Equal(WebSocketState.Open, accepted.State);

		using var rejected = new ClientWebSocket();
		rejected.Options.Cookies = cookies;
		rejected.Options.SetRequestHeader("Origin", "https://evil.example");
		await Assert.ThrowsAsync<WebSocketException>(() => rejected.ConnectAsync(
			new Uri($"ws://127.0.0.1:{fixture.Host.Port}/weavie-bridge"),
			cts.Token));
	}

	[Fact]
	public async Task Cookie_authenticates_same_origin_media_requests() {
		using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
		using var client = new HttpClient(handler);
		var connect = await client.PostAsync(
			fixture.Host.PageUrl,
			new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = Tokens.Correct }));
		Assert.Equal(HttpStatusCode.OK, connect.StatusCode);

		var media = await client.GetAsync($"{fixture.Host.BaseUrl}/weavie-media?session=missing&path=missing");
		Assert.Equal(HttpStatusCode.NotFound, media.StatusCode);
	}

	[Fact]
	public async Task Unknown_path_is_denied_by_default_without_a_token() {
		// Default-deny: a path that is neither a public asset nor a known route still requires the token.
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/api/secret");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Control_contract_requires_health_and_reports_generation() {
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/control/status?token={Tokens.Correct}");
		response.EnsureSuccessStatusCode();
		using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
		Assert.Equal(WorkspaceControlProtocol.SpawnContract, status.RootElement.GetProperty("spawnContract").GetInt32());

		var health = await Http.GetAsync($"{fixture.Host.BaseUrl}/control/health?token={Tokens.Correct}");
		health.EnsureSuccessStatusCode();
		using var payload = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
		Assert.True(payload.RootElement.GetProperty("healthy").GetBoolean());
	}

	[Fact]
	public async Task Bridge_broadcasts_pushes_to_every_connected_page() {
		// Two pages on one worker (a second tab, or a remote agent that loops back to the same worker) must BOTH
		// receive server pushes — a newcomer must never steal the stream from the others. Regression: the bridge
		// held a single socket, so a second connection silently starved the first of all output (input still
		// flowed over its own read loop, so the page "rendered once and then froze").
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
		using var first = await ConnectBridgeAsync(fixture.Host.Port, cts.Token);
		using var second = await ConnectBridgeAsync(fixture.Host.Port, cts.Token);

		string firstHello = HelloEnvelope("hello-first");
		string secondHello = HelloEnvelope("hello-second");
		await SendTextAsync(first, firstHello, cts.Token);
		await SendTextAsync(second, secondHello, cts.Token);
		var firstResponse = await ReceiveEnvelopeAsync(
			first,
			root => IsEnvelope(root, "host", "response", "connection", "hello", "hello-first"),
			cts.Token);
		await ReceiveEnvelopeAsync(
			second,
			root => IsEnvelope(root, "host", "response", "connection", "hello", "hello-second"),
			cts.Token);

		// A valid layout edit is a host event. The LayoutStore change publishes layout.state, which must reach
		// both physical peers even though only the second issued the mutation.
		var layout = firstResponse.GetProperty("payload").GetProperty("layout").Clone();
		await SendTextAsync(
			second,
			JsonSerializer.Serialize(new {
				scope = "host",
				session = (object?)null,
				kind = "event",
				requestId = (string?)null,
				feature = "layout",
				name = "changed",
				payload = new { document = layout },
				error = (string?)null,
			}),
			cts.Token);

		await ReceiveEnvelopeAsync(
			first,
			root => IsEnvelope(root, "host", "event", "layout", "state", null),
			cts.Token);
		await ReceiveEnvelopeAsync(
			second,
			root => IsEnvelope(root, "host", "event", "layout", "state", null),
			cts.Token);
	}

	private static async Task<ClientWebSocket> ConnectBridgeAsync(int port, CancellationToken ct) {
		var socket = new ClientWebSocket();
		await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/weavie-bridge?token={Tokens.Correct}"), ct);
		return socket;
	}

	private static Task SendTextAsync(ClientWebSocket socket, string json, CancellationToken ct) =>
		socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);

	private static string HelloEnvelope(string requestId) =>
		JsonSerializer.Serialize(new {
			scope = "host",
			session = (object?)null,
			kind = "request",
			requestId,
			feature = "connection",
			name = "hello",
			payload = new { },
			error = (string?)null,
		});

	private static bool IsEnvelope(
		JsonElement root,
		string scope,
		string kind,
		string feature,
		string name,
		string? requestId) =>
		root.GetProperty("scope").GetString() == scope
		&& root.GetProperty("kind").GetString() == kind
		&& root.GetProperty("feature").GetString() == feature
		&& root.GetProperty("name").GetString() == name
		&& (requestId is null
			? root.GetProperty("requestId").ValueKind == JsonValueKind.Null
			: root.GetProperty("requestId").GetString() == requestId);

	private static async Task<JsonElement> ReceiveEnvelopeAsync(
		ClientWebSocket socket,
		Func<JsonElement, bool> matches,
		CancellationToken ct) {
		byte[] buffer = new byte[64 * 1024];
		using var message = new MemoryStream();
		while (socket.State == WebSocketState.Open) {
			var result = await socket.ReceiveAsync(buffer, ct);
			if (result.MessageType == WebSocketMessageType.Close) {
				break;
			}

			message.Write(buffer, 0, result.Count);
			if (!result.EndOfMessage) {
				continue;
			}

			using var document = JsonDocument.Parse(
				new ReadOnlyMemory<byte>(message.GetBuffer(), 0, (int)message.Length));
			message.SetLength(0);
			if (matches(document.RootElement)) {
				return document.RootElement.Clone();
			}
		}

		throw new InvalidOperationException("The WebSocket closed before the expected envelope arrived.");
	}
}

/// <summary>Launches a real token-gated LOCAL loopback headless once for the suite.</summary>
public sealed class LocalHeadlessFixture : IAsyncLifetime {
	private readonly TempDirectory _workspace = new("weavie-local-tests");

	public HostHandle Host { get; private set; } = null!;

	public async Task InitializeAsync() {
		int port = Hosts.FreePort();
		Host = await HostHandle.StartAsync(
			Hosts.HeadlessDll,
			["--port", port.ToString(), "--workspace", _workspace.Path],
			port,
			readyMarker: "open  http://",
			timeout: TimeSpan.FromSeconds(60));
	}

	public async Task DisposeAsync() {
		await Host.DisposeAsync();
		_workspace.Dispose();
	}
}

/// <summary>
/// Local mode is loopback-only and uses a server-minted token, so ambient web pages cannot read workspace files.
/// </summary>
public sealed class HeadlessLocalAuthTests(LocalHeadlessFixture fixture) : IClassFixture<LocalHeadlessFixture> {
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

	[Fact]
	public async Task Document_shows_the_connect_page_in_local_mode() {
		var response = await Http.GetAsync(fixture.Host.PageUrl);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("Connect to Weavie", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Generated_token_connects_on_the_clean_url_in_local_mode() {
		using var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
		using var client = new HttpClient(handler);
		var response = await client.PostAsync(
			fixture.Host.PageUrl,
			new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = fixture.Host.Token }));
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.DoesNotContain(
			"Connect to Weavie",
			await response.Content.ReadAsStringAsync(),
			StringComparison.Ordinal);
	}

	[Fact]
	public async Task Bridge_websocket_upgrade_is_rejected_with_a_foreign_origin() {
		// A foreign browser tab without the generated token is refused before the WebSocket upgrade.
		using var socket = new ClientWebSocket();
		socket.Options.SetRequestHeader("Origin", "http://evil.example");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await Assert.ThrowsAsync<WebSocketException>(() =>
			socket.ConnectAsync(new Uri($"ws://127.0.0.1:{fixture.Host.Port}/weavie-bridge"), cts.Token));
	}

	[Fact]
	public async Task Bridge_websocket_upgrade_succeeds_with_a_matching_origin() {
		using var socket = new ClientWebSocket();
		socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{fixture.Host.Port}");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{fixture.Host.Port}/weavie-bridge?token={fixture.Host.Token}"), cts.Token);
		Assert.Equal(WebSocketState.Open, socket.State);
		try {
			await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cts.Token);
		} catch (WebSocketException) {
		}
	}
}

/// <summary>
/// Fail-closed startup guard: a host binds the network only via explicit remote mode, which mandates a
/// token. Every unsafe flag combination refuses to start (non-zero exit), so an exposed unauthenticated
/// host can never come up.
/// </summary>
public sealed class HeadlessStartupGuardTests {
	[Theory]
	[InlineData("bind-without-remote")]   // network bind without --remote
	[InlineData("remote-without-token")]  // remote without a token
	[InlineData("token-without-remote")]  // token without --remote
	[InlineData("remote-without-contract")]
	[InlineData("remote-with-wrong-contract")]
	public async Task Refuses_to_start_for_unsafe_flag_combinations(string scenario) {
		string[] extra = scenario switch {
			"bind-without-remote" => ["--bind", "0.0.0.0"],
			"remote-without-token" => ["--remote"],
			"token-without-remote" => ["--token", "abc"],
			"remote-without-contract" => ["--remote", "--token", "abc"],
			"remote-with-wrong-contract" => ["--remote", "--token", "abc", "--spawn-contract",
				(WorkspaceControlProtocol.SpawnContract + 1).ToString()],
			_ => throw new ArgumentOutOfRangeException(nameof(scenario)),
		};
		var args = new List<string> { "--port", Hosts.FreePort().ToString(), "--workspace", Path.GetTempPath() };
		args.AddRange(extra);

		var (exitCode, output) = await HostHandle.RunToExitAsync(Hosts.HeadlessDll, args, TimeSpan.FromSeconds(45));

		Assert.NotEqual(0, exitCode);
		Assert.Contains("weavie-headless", output, StringComparison.Ordinal);
	}
}
