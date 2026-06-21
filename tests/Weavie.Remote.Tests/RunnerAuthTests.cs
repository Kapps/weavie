using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Weavie.Remote.Tests;

/// <summary>Launches a real <c>Weavie.Runner</c> once for the suite (workspace need not be a git repo for auth probing).</summary>
public sealed class RunnerFixture : IAsyncLifetime {
	private readonly string _workspace =
		Path.Combine(Path.GetTempPath(), "weavie-runner-tests", Guid.NewGuid().ToString("N"));

	public HostHandle Host { get; private set; } = null!;

	public async Task InitializeAsync() {
		Directory.CreateDirectory(_workspace);
		int port = Hosts.FreePort();
		Host = await HostHandle.StartAsync(
			Hosts.RunnerDll,
			[
				"--workspace", _workspace, "--token", Tokens.Correct, "--port", port.ToString(),
				"--bind", "127.0.0.1", "--worker-bind", "127.0.0.1", "--headless", Hosts.HeadlessDll,
			],
			port,
			readyMarker: "control plane: http://",
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
/// Black-box auth against the real runner control plane: every endpoint rejects every bad token (via both
/// <c>?token=</c> and <c>Authorization: Bearer</c>) and unknown paths, accepts only the correct token, and
/// answers the CORS preflight without auth (so browsers aren't broken). The default-deny gate can't be bypassed.
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
	public async Task Backend_is_served_with_correct_query_token() {
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/backend?token={Tokens.Correct}");
		Assert.Equal(HttpStatusCode.OK, response.StatusCode);
	}

	[Fact]
	public async Task Unknown_path_is_denied_by_default_without_a_token() {
		var response = await Http.GetAsync($"{fixture.Host.BaseUrl}/foo");
		Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
	}

	[Fact]
	public async Task Landing_page_is_denied_without_a_token_and_served_with_it() {
		Assert.Equal(HttpStatusCode.Unauthorized, (await Http.GetAsync($"{fixture.Host.BaseUrl}/")).StatusCode);
		Assert.Equal(HttpStatusCode.OK, (await Http.GetAsync($"{fixture.Host.BaseUrl}/?token={Tokens.Correct}")).StatusCode);
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
}
