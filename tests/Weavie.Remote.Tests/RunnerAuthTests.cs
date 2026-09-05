using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace Weavie.Remote.Tests;

/// <summary>Launches a real <c>Weavie.Runner</c> once for the suite (workspace need not be a git repo for auth probing).</summary>
public sealed class RunnerFixture : IAsyncLifetime {
	private readonly TempDirectory _workspace = new("weavie-runner-tests");

	public HostHandle Host { get; private set; } = null!;

	public async Task InitializeAsync() =>
		Host = await StartRunnerAsync(_workspace.Path, Tokens.Correct, workerPort: null);

	internal static async Task<HostHandle> StartRunnerAsync(string workspace, string token, int? workerPort) {
		int port;
		do {
			port = Hosts.FreePort();
		} while (port == workerPort);
		var args = new List<string> {
			"--workspace", workspace, "--token", token, "--port", port.ToString(),
			"--bind", "127.0.0.1", "--worker-bind", "127.0.0.1", "--headless", Hosts.HeadlessDll,
		};
		if (workerPort is { } pinnedPort) {
			args.AddRange(["--worker-port", pinnedPort.ToString()]);
		}

		var host = await HostHandle.StartAsync(
			Hosts.RunnerDll,
			args,
			port,
			readyMarker: "control plane: http://",
			timeout: TimeSpan.FromSeconds(60));
		try {
			await WaitForWorkerAsync(host, token);
			return host;
		} catch {
			await host.DisposeAsync();
			throw;
		}
	}

	public async Task DisposeAsync() {
		await Host.DisposeAsync();
		_workspace.Dispose();
	}

	private static async Task WaitForWorkerAsync(HostHandle host, string token) {
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
		while (DateTime.UtcNow < deadline) {
			using var request = new HttpRequestMessage(HttpMethod.Get, $"{host.BaseUrl}/backend");
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			using var response = await http.SendAsync(request);
			if (response.IsSuccessStatusCode) {
				using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
				if (body.RootElement.GetProperty("status").GetString() == "running") {
					return;
				}
			}

			await Task.Delay(100);
		}

		throw new TimeoutException("runner worker did not become ready for browser auth tests");
	}
}

/// <summary>
/// Black-box auth against the real runner: the browser root establishes persistent cookies and hands off to
/// the worker, while the permissive-CORS machine API remains explicit-Bearer-only and default-deny.
/// </summary>
public sealed class RunnerAuthTests(RunnerFixture fixture) : IClassFixture<RunnerFixture> {
	private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

	[Theory]
	[MemberData(nameof(Tokens.Denied), MemberType = typeof(Tokens))]
	public async Task Backend_is_denied_for_bad_query_token(string variant) {
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/backend{Tokens.QuerySuffix(variant)}");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Theory]
	[MemberData(nameof(Tokens.Denied), MemberType = typeof(Tokens))]
	public async Task Backend_is_denied_for_bad_bearer_token(string variant) {
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{fixture.Host.BaseUrl}/backend");
		string? token = Tokens.Value(variant);
		if (token is not null) {
			// "Bearer " with an empty/short/wrong value must still be rejected.
			request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
		}

		var response = await Http.SendAsync(request);
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Backend_is_served_with_correct_bearer_token() {
		using var request = new HttpRequestMessage(HttpMethod.Get, $"{fixture.Host.BaseUrl}/backend");
		request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.Correct);
		var response = await Http.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("\"url\"", await response.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Backend_rejects_the_correct_token_in_the_query() {
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/backend?token={Tokens.Correct}");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Unknown_path_is_denied_by_default_without_a_token() {
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/foo");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Landing_page_shows_the_connect_form() {
		using var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains("Runner token", await response.Content.ReadAsStringAsync());
		Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
	}

	[Fact]
	public async Task Landing_page_scrubs_query_tokens_from_the_browser_url() {
		using var handler = new HttpClientHandler { AllowAutoRedirect = false };
		using var client = new HttpClient(handler);
		using var response = await client.GetAsync($"{fixture.Host.BaseUrl}/?token={Tokens.Correct}");
		Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
		Assert.Equal("/", response.Headers.Location?.OriginalString);
		Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
	}

	[Theory]
	[MemberData(nameof(Tokens.Denied), MemberType = typeof(Tokens))]
	public async Task Wrong_connect_token_is_rejected(string variant) {
		using var handler = new HttpClientHandler { AllowAutoRedirect = false };
		using var client = new HttpClient(handler);
		string token = Tokens.Value(variant) ?? string.Empty;
		using var response = await client.PostAsync(
			$"{fixture.Host.BaseUrl}/",
			new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
		Assert.Contains("That token was not accepted", await response.Content.ReadAsStringAsync());
		Assert.False(response.Headers.TryGetValues("Set-Cookie", out _));
	}

	[Fact]
	public async Task Correct_token_is_remembered_and_hands_off_to_the_clean_worker() {
		var cookies = new CookieContainer();
		using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies };
		using var client = new HttpClient(handler);
		var workerUrl = await ConnectBrowserAsync(client, fixture.Host.BaseUrl);

		using var app = await client.GetAsync(workerUrl);
		Assert.Equal(HttpStatusCode.OK, app.StatusCode);
		Assert.DoesNotContain("Workspace token", await app.Content.ReadAsStringAsync());
	}

	[Fact]
	public async Task Installed_worker_cookie_survives_a_runner_restart() {
		using var workspace = new TempDirectory("weavie-runner-restart-tests");
		int workerPort = Hosts.FreePort();
		var cookies = new CookieContainer();
		using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies };
		using var client = new HttpClient(handler);
		Uri workerUrl;
		await using (var first = await RunnerFixture.StartRunnerAsync(workspace.Path, Tokens.Correct, workerPort)) {
			workerUrl = await ConnectBrowserAsync(client, first.BaseUrl);
			Assert.Equal(workerPort, workerUrl.Port);
		}

		await using (await RunnerFixture.StartRunnerAsync(workspace.Path, Tokens.Correct, workerPort)) {
			using var app = await client.GetAsync(workerUrl);
			Assert.Equal(HttpStatusCode.OK, app.StatusCode);
			Assert.DoesNotContain("Workspace token", await app.Content.ReadAsStringAsync());
		}
	}

	[Fact]
	public async Task Browser_cookie_does_not_authorize_the_machine_api() {
		var cookies = new CookieContainer();
		using var handler = new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies };
		using var client = new HttpClient(handler);
		using var connect = await client.PostAsync(
			$"{fixture.Host.BaseUrl}/",
			new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = Tokens.Correct }));
		Assert.Equal(HttpStatusCode.OK, connect.StatusCode);

		using var response = await client.GetAsync($"{fixture.Host.BaseUrl}/backend");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Https_forward_sets_a_secure_runner_cookie() {
		using var handler = new HttpClientHandler { AllowAutoRedirect = false };
		using var client = new HttpClient(handler);
		using var request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.Host.BaseUrl}/") {
			Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = Tokens.Correct }),
		};
		request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
		using var response = await client.SendAsync(request);
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
		Assert.Contains(
			response.Headers.GetValues("Set-Cookie"),
			value => value.Contains("Secure", StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public async Task Cors_preflight_is_allowed_without_a_token() {
		// The preflight carries no credentials, so blocking it would stop browsers reaching the control plane.
		// It returns 204 + the permissive CORS origin and exposes nothing sensitive.
		using var request = new HttpRequestMessage(HttpMethod.Options, $"{fixture.Host.BaseUrl}/backend");
		var response = await Http.SendAsync(request);
		Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
		Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
	}

	private static async Task<Uri> ConnectBrowserAsync(HttpClient client, string runnerUrl) {
		using var connect = await client.PostAsync(
			$"{runnerUrl}/",
			new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = Tokens.Correct }));
		Assert.Equal(HttpStatusCode.OK, connect.StatusCode);
		Assert.Contains("http-equiv=\"refresh\" content=\"1;url=/\"", await connect.Content.ReadAsStringAsync());
		AssertPersistentCookie(connect, "weavie-runner-");

		using var handoff = await client.GetAsync($"{runnerUrl}/");
		Assert.Equal(HttpStatusCode.Redirect, handoff.StatusCode);
		var workerUrl = Assert.IsType<Uri>(handoff.Headers.Location);
		Assert.True(workerUrl.IsAbsoluteUri);
		Assert.Equal(string.Empty, workerUrl.Query);
		Assert.Equal(string.Empty, workerUrl.Fragment);
		AssertPersistentCookie(handoff, "weavie-");
		return workerUrl;
	}

	private static void AssertPersistentCookie(HttpResponseMessage response, string namePrefix) {
		Assert.Contains(response.Headers.GetValues("Set-Cookie"), value => {
			string normalized = value.ToLowerInvariant();
			return normalized.StartsWith(namePrefix, StringComparison.Ordinal)
				&& normalized.Contains("httponly", StringComparison.Ordinal)
				&& normalized.Contains("samesite=strict", StringComparison.Ordinal)
				&& normalized.Contains("max-age=31536000", StringComparison.Ordinal);
		});
	}
}
