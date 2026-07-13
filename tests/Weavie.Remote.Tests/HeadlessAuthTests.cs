using System.Net;
using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace Weavie.Remote.Tests;

/// <summary>Launches a real network-exposed (remote-mode) <c>Weavie.Headless</c> worker once for the suite.</summary>
public sealed class RemoteHeadlessFixture : IAsyncLifetime {
	private readonly string _workspace =
		Path.Combine(Path.GetTempPath(), "weavie-remote-tests", Guid.NewGuid().ToString("N"));

	public HostHandle Host { get; private set; } = null!;

	public async Task InitializeAsync() {
		Directory.CreateDirectory(_workspace);
		int port = Hosts.FreePort();
		Host = await HostHandle.StartAsync(
			Hosts.HeadlessDll,
			["--remote", "--bind", "127.0.0.1", "--port", port.ToString(), "--token", Tokens.Correct, "--workspace", _workspace],
			port,
			readyMarker: "open  http://",
			timeout: TimeSpan.FromSeconds(60));
	}

	public async Task DisposeAsync() {
		await Host.DisposeAsync();
		try {
			Directory.Delete(_workspace, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}
}

/// <summary>
/// Black-box auth against a real remote-mode headless worker: document, bridge, and unknown paths reject
/// every bad token shape and accept only the correct one. No token shape bypasses auth.
/// </summary>
public sealed class HeadlessRemoteAuthTests(RemoteHeadlessFixture fixture) : IClassFixture<RemoteHeadlessFixture> {
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

	[Theory]
	[MemberData(nameof(Tokens.Denied), MemberType = typeof(Tokens))]
	public async Task Document_is_denied_without_a_valid_token(string variant) {
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/{Tokens.QuerySuffix(variant)}");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Document_is_served_with_the_correct_token() {
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/?token={Tokens.Correct}");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
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
		// https://weavie.dev, the runner's picker page on another port), so a foreign Origin + correct token must
		// connect. The same-origin (CSWSH) check applies to the local no-token mode only. Regression: the
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
	public async Task Unknown_path_is_denied_by_default_without_a_token() {
		// Default-deny: a path that is neither a public asset nor a known route still requires the token.
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/api/secret");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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

		// `ready` makes the host push its restore state (the session list among it); with broadcast it reaches
		// every socket, so `first` — which sent nothing itself — must still receive the push `second` triggered.
		await SendTextAsync(second, "{\"type\":\"ready\"}", cts.Token);

		Assert.True(
			await ReceivesTypeAsync(first, "session-list", cts.Token),
			"the first page received no broadcast push after a second page connected");
		Assert.True(
			await ReceivesTypeAsync(second, "session-list", cts.Token),
			"the second page received no push for its own ready");
	}

	private static async Task<ClientWebSocket> ConnectBridgeAsync(int port, CancellationToken ct) {
		var socket = new ClientWebSocket();
		await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/weavie-bridge?token={Tokens.Correct}"), ct);
		return socket;
	}

	private static Task SendTextAsync(ClientWebSocket socket, string json, CancellationToken ct) =>
		socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);

	/// <summary>Reads bridge frames until one is a message of the given <paramref name="type"/>, or the token cancels (→ false).</summary>
	private static async Task<bool> ReceivesTypeAsync(ClientWebSocket socket, string type, CancellationToken ct) {
		string needle = $"\"type\":\"{type}\"";
		byte[] buffer = new byte[64 * 1024];
		using var message = new MemoryStream();
		try {
			while (socket.State == WebSocketState.Open) {
				var result = await socket.ReceiveAsync(buffer, ct);
				if (result.MessageType == WebSocketMessageType.Close) {
					return false;
				}

				message.Write(buffer, 0, result.Count);
				if (!result.EndOfMessage) {
					continue;
				}

				string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
				message.SetLength(0);
				if (text.Contains(needle, StringComparison.Ordinal)) {
					return true;
				}
			}
		} catch (OperationCanceledException) {
			return false;
		}

		return false;
	}
}

/// <summary>Launches a real token-gated LOCAL loopback headless once for the suite.</summary>
public sealed class LocalHeadlessFixture : IAsyncLifetime {
	private readonly string _workspace =
		Path.Combine(Path.GetTempPath(), "weavie-local-tests", Guid.NewGuid().ToString("N"));

	public HostHandle Host { get; private set; } = null!;

	public async Task InitializeAsync() {
		Directory.CreateDirectory(_workspace);
		int port = Hosts.FreePort();
		Host = await HostHandle.StartAsync(
			Hosts.HeadlessDll,
			["--port", port.ToString(), "--workspace", _workspace],
			port,
			readyMarker: "open  http://",
			timeout: TimeSpan.FromSeconds(60));
	}

	public async Task DisposeAsync() {
		await Host.DisposeAsync();
		try {
			Directory.Delete(_workspace, recursive: true);
		} catch (IOException) {
		} catch (UnauthorizedAccessException) {
		}
	}
}

/// <summary>
/// Local mode is loopback-only and uses a server-minted token, so ambient web pages cannot read workspace files.
/// </summary>
public sealed class HeadlessLocalAuthTests(LocalHeadlessFixture fixture) : IClassFixture<LocalHeadlessFixture> {
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

	[Fact]
	public async Task Document_is_served_with_the_generated_token_in_local_mode() {
		var response = await Http.GetAsync(fixture.Host.PageUrl);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task Document_is_denied_without_the_generated_token_in_local_mode() {
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
		string token = new Uri(fixture.Host.PageUrl).Query.TrimStart('?');
		using var socket = new ClientWebSocket();
		socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{fixture.Host.Port}");
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
		await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{fixture.Host.Port}/weavie-bridge?{token}"), cts.Token);
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
	public async Task Refuses_to_start_for_unsafe_flag_combinations(string scenario) {
		string[] extra = scenario switch {
			"bind-without-remote" => ["--bind", "0.0.0.0"],
			"remote-without-token" => ["--remote"],
			"token-without-remote" => ["--token", "abc"],
			_ => throw new ArgumentOutOfRangeException(nameof(scenario)),
		};
		var args = new List<string> { "--port", Hosts.FreePort().ToString(), "--workspace", Path.GetTempPath() };
		args.AddRange(extra);

		var (exitCode, output) = await HostHandle.RunToExitAsync(Hosts.HeadlessDll, args, TimeSpan.FromSeconds(45));

		Assert.NotEqual(0, exitCode);
		Assert.Contains("weavie-headless", output, StringComparison.Ordinal);
	}
}
