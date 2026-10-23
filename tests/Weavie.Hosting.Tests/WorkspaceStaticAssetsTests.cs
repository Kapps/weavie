using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Weavie.Hosting.Web;
using Weavie.TestSupport;
using Xunit;

namespace Weavie.Hosting.Tests;

public sealed class WorkspaceStaticAssetsTests : IAsyncLifetime {
	private readonly TempDirectory _root = new();
	private WebApplication? _app;
	private HttpClient? _client;

	public async Task InitializeAsync() {
		Directory.CreateDirectory(_root.Combine("assets"));
		await File.WriteAllTextAsync(_root.Combine("assets", "main-abc.js"), "plain");
		await File.WriteAllTextAsync(_root.Combine("assets", "main-abc.js.br"), "brotli");
		await File.WriteAllTextAsync(_root.Combine("manifest.webmanifest"), "{}");
		var builder = WorkspaceHttpServer.CreateApplicationBuilder();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		_app = builder.Build();
		WorkspaceStaticAssets.Use(_app, new PhysicalFileProvider(_root.Path));
		await _app.StartAsync();
		_client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
	}

	public async Task DisposeAsync() {
		_client?.Dispose();
		if (_app is not null) {
			await _app.DisposeAsync();
		}
		_root.Dispose();
	}

	[Fact]
	public async Task BrotliClientGetsTheCompressedSiblingAsImmutable() {
		using var response = await GetAsync("/assets/main-abc.js", "gzip, br");

		Assert.Equal("brotli", await response.Content.ReadAsStringAsync());
		Assert.Equal(["br"], response.Content.Headers.ContentEncoding);
		Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
		Assert.Equal(["Accept-Encoding"], response.Headers.Vary);
		Assert.True(response.Headers.CacheControl?.Public);
		Assert.True(response.Headers.CacheControl?.MaxAge > TimeSpan.FromDays(300));
	}

	[Theory]
	[InlineData("gzip")]
	[InlineData("br;q=0")]
	public async Task OtherClientsGetThePlainFile(string acceptEncoding) {
		using var response = await GetAsync("/assets/main-abc.js", acceptEncoding);

		Assert.Equal("plain", await response.Content.ReadAsStringAsync());
		Assert.Empty(response.Content.Headers.ContentEncoding);
		Assert.Equal(["Accept-Encoding"], response.Headers.Vary);
	}

	[Fact]
	public async Task UnhashedFilesRevalidate() {
		using var response = await GetAsync("/manifest.webmanifest", "br");

		Assert.True(response.Headers.CacheControl?.NoCache);
		Assert.Empty(response.Content.Headers.ContentEncoding);
	}

	private Task<HttpResponseMessage> GetAsync(string path, string acceptEncoding) {
		var request = new HttpRequestMessage(HttpMethod.Get, path);
		request.Headers.AcceptEncoding.ParseAdd(acceptEncoding);
		return _client!.SendAsync(request);
	}
}
