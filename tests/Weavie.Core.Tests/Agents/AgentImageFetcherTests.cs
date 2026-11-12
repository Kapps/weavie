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

	private sealed class StubHandler(string type, byte[] body) : HttpMessageHandler {
		public int Requests { get; private set; }
		public bool HideLength { get; init; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
			Requests++;
			HttpContent content = HideLength ? new StreamContent(new MemoryStream(body)) : new ByteArrayContent(body);
			content.Headers.ContentType = new MediaTypeHeaderValue(type);
			if (HideLength) content.Headers.ContentLength = null;
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
		}
	}
}
