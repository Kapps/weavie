using System.Net;
using System.Net.Http.Headers;
using Weavie.Core.Agents;
using Xunit;

namespace Weavie.Core.Tests;

public sealed class AgentImageFetcherTests {
	[Fact]
	public async Task LoadsAnHttpsRasterImage() {
		using var fetcher = new AgentImageFetcher(new StubHandler("image/png", [1, 2, 3]));

		var image = await fetcher.FetchAsync("https://example.com/a.png", enabled: true, CancellationToken.None);

		Assert.Equal("image/png", image.ContentType);
		Assert.Equal([1, 2, 3], image.Bytes);
	}

	[Theory]
	[InlineData("http://example.com/a.png", "Only https images")]
	[InlineData("https://example.com:8443/a.png", "Only https images")]
	[InlineData("https://user:secret@example.com/a.png", "Only https images")]
	[InlineData("not a url", "Only https images")]
	public async Task RefusesAddressesThatAreNotPlainHttps(string address, string reason) {
		var handler = new StubHandler("image/png", [1]);
		using var fetcher = new AgentImageFetcher(handler);

		var refused = await Assert.ThrowsAsync<AgentImageRefusedException>(() => fetcher.FetchAsync(address, enabled: true, CancellationToken.None));

		Assert.StartsWith(reason, refused.Message, StringComparison.Ordinal);
		Assert.Equal(0, handler.Requests);
	}

	[Theory]
	[InlineData("image/svg+xml", "The address served image/svg+xml, which is not a raster image.")]
	[InlineData("text/html", "The address served text/html, which is not a raster image.")]
	public async Task RefusesWhatIsNotARasterImage(string type, string reason) {
		using var fetcher = new AgentImageFetcher(new StubHandler(type, [1]));

		var refused = await Assert.ThrowsAsync<AgentImageRefusedException>(() => fetcher.FetchAsync("https://example.com/a", enabled: true, CancellationToken.None));

		Assert.Equal(reason, refused.Message);
	}

	[Fact]
	public async Task RefusesAnImageOverTheLimit_EvenWithoutALength() {
		using var fetcher = new AgentImageFetcher(new StubHandler("image/png", new byte[AgentImageFetcher.MaxBytes + 1]) { HideLength = true });

		var refused = await Assert.ThrowsAsync<AgentImageRefusedException>(() => fetcher.FetchAsync("https://example.com/a.png", enabled: true, CancellationToken.None));

		Assert.Equal("The image is larger than the 20 MiB limit.", refused.Message);
	}

	[Fact]
	public async Task RefusesEverythingWhenTheSettingIsOff() {
		var handler = new StubHandler("image/png", [1]);
		using var fetcher = new AgentImageFetcher(handler);

		var refused = await Assert.ThrowsAsync<AgentImageRefusedException>(() => fetcher.FetchAsync("https://example.com/a.png", enabled: false, CancellationToken.None));

		Assert.Equal("Remote images are off (setting agent.remoteImages).", refused.Message);
		Assert.Equal(0, handler.Requests);
	}

	[Theory]
	[InlineData("https://10.0.0.5:8443/a.png", "Only https images")]
	[InlineData("http://example.com/b.png", "Only https images")]
	public async Task ARedirectMustPassTheSameAddressRule(string target, string reason) {
		var handler = new StubHandler("image/png", [1]) { Redirects = { ["https://example.com/a.png"] = target } };
		using var fetcher = new AgentImageFetcher(handler);

		var refused = await Assert.ThrowsAsync<AgentImageRefusedException>(() => fetcher.FetchAsync("https://example.com/a.png", enabled: true, CancellationToken.None));

		Assert.StartsWith(reason, refused.Message, StringComparison.Ordinal);
		Assert.Equal(1, handler.Requests);
	}

	[Fact]
	public async Task FollowsAnAllowedRedirect_AndRefusesALoop() {
		var handler = new StubHandler("image/png", [7]) { Redirects = { ["https://example.com/a.png"] = "/b.png" } };
		using var fetcher = new AgentImageFetcher(handler);
		Assert.Equal([7], (await fetcher.FetchAsync("https://example.com/a.png", enabled: true, CancellationToken.None)).Bytes);

		var looping = new StubHandler("image/png", [1]) { Redirects = { ["https://example.com/a.png"] = "/a.png" } };
		using var loop = new AgentImageFetcher(looping);
		var refused = await Assert.ThrowsAsync<AgentImageRefusedException>(() => loop.FetchAsync("https://example.com/a.png", enabled: true, CancellationToken.None));
		Assert.Equal($"The image redirected more than {AgentImageFetcher.MaxRedirects} times.", refused.Message);
	}

	[Fact]
	public async Task ABodyThatBreaksOffIsARefusal() {
		using var fetcher = new AgentImageFetcher(new StubHandler("image/png", [1]) { Broken = true });

		var refused = await Assert.ThrowsAsync<AgentImageRefusedException>(() => fetcher.FetchAsync("https://example.com/a.png", enabled: true, CancellationToken.None));

		Assert.StartsWith("The image could not be fetched:", refused.Message, StringComparison.Ordinal);
	}

	private sealed class StubHandler(string type, byte[] body) : HttpMessageHandler {
		public int Requests { get; private set; }
		public bool HideLength { get; init; }
		public bool Broken { get; init; }
		public Dictionary<string, string> Redirects { get; } = [];

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
			Requests++;
			if (Redirects.TryGetValue(request.RequestUri!.ToString(), out string? target)) {
				var redirect = new HttpResponseMessage(HttpStatusCode.Found);
				redirect.Headers.Location = new Uri(target, UriKind.RelativeOrAbsolute);
				return Task.FromResult(redirect);
			}
			if (Broken) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new BrokenContent(type) });
			HttpContent content = HideLength ? new StreamContent(new MemoryStream(body)) : new ByteArrayContent(body);
			content.Headers.ContentType = new MediaTypeHeaderValue(type);
			if (HideLength) content.Headers.ContentLength = null;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
		}
	}

	private sealed class BrokenContent : HttpContent {
		public BrokenContent(string type) {
			Headers.ContentType = new MediaTypeHeaderValue(type);
		}

		protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => throw new IOException("connection reset");
		protected override bool TryComputeLength(out long length) {
			length = 0;
			return false;
		}
	}
}
