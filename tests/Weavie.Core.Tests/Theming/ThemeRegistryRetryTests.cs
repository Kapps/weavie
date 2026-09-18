using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Weavie.Core.Theming;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class ThemeRegistryRetryTests {
	[Theory]
	[InlineData(408)]
	[InlineData(429)]
	[InlineData(500)]
	[InlineData(502)]
	[InlineData(503)]
	[InlineData(504)]
	public async Task Search_RecoversFromTransientStatus(int status) {
		using var handler = new ScriptedHandler(attempt => Response(attempt == 1 ? status : 200));
		using var http = new HttpClient(handler);
		await SearchAsync(http, CancellationToken.None);
		Assert.Equal(2, handler.Attempts);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task Search_RecoversFromConnectionFailureOrTimeout(bool timeout) {
		using var handler = new ScriptedHandler(attempt => attempt == 1
			? throw (timeout ? new TaskCanceledException() : new HttpRequestException("Connection lost"))
			: Response(200));
		using var http = new HttpClient(handler);
		await SearchAsync(http, CancellationToken.None);
		Assert.Equal(2, handler.Attempts);
	}

	[Theory]
	[InlineData(400, 1)]
	[InlineData(401, 1)]
	[InlineData(404, 1)]
	[InlineData(503, 3)]
	public async Task Search_SurfacesPermanentOrExhaustedFailure(int status, int attempts) {
		using var handler = new ScriptedHandler(_ => Response(status));
		using var http = new HttpClient(handler);
		var error = await Assert.ThrowsAsync<HttpRequestException>(() => SearchAsync(http, CancellationToken.None));
		Assert.Equal((HttpStatusCode)status, error.StatusCode);
		Assert.Equal(attempts, handler.Attempts);
	}

	[Fact]
	public async Task Search_DoesNotRetryBeforeLongRetryAfter() {
		using var handler = new ScriptedHandler(_ => {
			var response = Response(429);
			response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
			return response;
		});
		using var http = new HttpClient(handler);
		await Assert.ThrowsAsync<HttpRequestException>(() => SearchAsync(http, CancellationToken.None));
		Assert.Equal(1, handler.Attempts);
	}

	[Fact]
	public async Task Search_CancellationStopsPendingRetry() {
		using var handler = new ScriptedHandler(_ => Response(503));
		using var http = new HttpClient(handler);
		using var cancellation = new CancellationTokenSource();
		var search = SearchAsync(http, cancellation.Token);
		Assert.Equal(1, handler.Attempts);
		Assert.False(search.IsCompleted);
		await cancellation.CancelAsync();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => search);
		Assert.Equal(1, handler.Attempts);
	}

	[Fact]
	public async Task Search_DoesNotRetryMalformedJson() {
		using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) {
			Content = new StringContent("not json"),
		});
		using var http = new HttpClient(handler);
		await Assert.ThrowsAnyAsync<JsonException>(() => SearchAsync(http, CancellationToken.None));
		Assert.Equal(1, handler.Attempts);
	}

	private static Task<JsonElement> SearchAsync(HttpClient http, CancellationToken ct) =>
		new OpenVsxThemeInstaller(http, "https://registry.test").SearchAsync("", 0, "downloadCount", ct);

	private static HttpResponseMessage Response(int status) => new((HttpStatusCode)status) {
		Content = new StringContent("{}"),
	};

	private sealed class ScriptedHandler(Func<int, HttpResponseMessage> response) : HttpMessageHandler {
		public int Attempts { get; private set; }
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(response(++Attempts));
	}
}
