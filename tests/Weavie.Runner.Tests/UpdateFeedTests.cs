using System.Net;
using Xunit;

namespace Weavie.Runner.Tests;

public sealed class UpdateFeedTests {
	[Fact]
	public async Task StableResolvesItsAnnotatedTagToTheImmutableRelease() {
		using var handler = new FeedHandler(
			("/git/ref/tags/stable", HttpStatusCode.OK, """{"object":{"type":"tag","sha":"version-object"}}"""),
			("/git/tags/version-object", HttpStatusCode.OK, """{"tag":"v0.2.1"}"""),
			("/releases/tags/v0.2.1", HttpStatusCode.OK, """{"id":1507,"assets":[]}"""));
		using var http = new HttpClient(handler);
		using var release = await UpdateFeed.ReadAsync(http, UpdateChannel.Stable, CancellationToken.None);
		Assert.NotNull(release);
		Assert.Equal(1507, release.RootElement.GetProperty("id").GetInt32());
		handler.AssertComplete();
	}

	[Fact]
	public async Task LatestReadsTheRollingReleaseDirectly() {
		using var handler = new FeedHandler(("/releases/tags/main-latest", HttpStatusCode.OK, """{"id":42}"""));
		using var http = new HttpClient(handler);
		using var release = await UpdateFeed.ReadAsync(http, UpdateChannel.Latest, CancellationToken.None);
		Assert.NotNull(release);
		Assert.Equal(42, release.RootElement.GetProperty("id").GetInt32());
		handler.AssertComplete();
	}

	[Theory]
	[InlineData(UpdateChannel.Stable, "/git/ref/tags/stable")]
	[InlineData(UpdateChannel.Latest, "/releases/tags/main-latest")]
	public async Task AnUnpublishedChannelHasNoUpdate(UpdateChannel channel, string path) {
		using var handler = new FeedHandler((path, HttpStatusCode.NotFound, "{}"));
		using var http = new HttpClient(handler);
		Assert.Null(await UpdateFeed.ReadAsync(http, channel, CancellationToken.None));
		handler.AssertComplete();
	}

	[Theory]
	[InlineData("main-latest")]
	[InlineData("v0.2")]
	[InlineData("v0.2.1.1507")]
	[InlineData("v00.2.1")]
	[InlineData("v0.2.1-rc.1")]
	[InlineData("")]
	public async Task StableRejectsMalformedVersionTags(string tag) {
		using var handler = new FeedHandler(
			("/git/ref/tags/stable", HttpStatusCode.OK, """{"object":{"type":"tag","sha":"version-object"}}"""),
			("/git/tags/version-object", HttpStatusCode.OK, $$"""{"tag":"{{tag}}"}"""));
		using var http = new HttpClient(handler);
		await Assert.ThrowsAsync<InvalidDataException>(() => UpdateFeed.ReadAsync(http, UpdateChannel.Stable, CancellationToken.None));
		handler.AssertComplete();
	}

	[Fact]
	public async Task StableRejectsALightweightTag() {
		using var handler = new FeedHandler(
			("/git/ref/tags/stable", HttpStatusCode.OK, """{"object":{"type":"commit","sha":"commit"}}"""));
		using var http = new HttpClient(handler);
		await Assert.ThrowsAsync<InvalidDataException>(() => UpdateFeed.ReadAsync(http, UpdateChannel.Stable, CancellationToken.None));
		handler.AssertComplete();
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("{\"object\":null}")]
	[InlineData("{\"object\":{\"type\":42}}")]
	[InlineData("{\"object\":{\"type\":\"tag\"}}")]
	public async Task MalformedStableReferenceBecomesAVisibleUpdateError(string body) {
		using var handler = new FeedHandler(("/git/ref/tags/stable", HttpStatusCode.OK, body));
		using var http = new HttpClient(handler);
		await Assert.ThrowsAsync<InvalidDataException>(() => UpdateFeed.ReadAsync(http, UpdateChannel.Stable, CancellationToken.None));
		handler.AssertComplete();
	}

	[Theory]
	[InlineData("{}")]
	[InlineData("{\"tag\":42}")]
	public async Task MalformedTagObjectBecomesAVisibleUpdateError(string body) {
		using var handler = new FeedHandler(
			("/git/ref/tags/stable", HttpStatusCode.OK, """{"object":{"type":"tag","sha":"version-object"}}"""),
			("/git/tags/version-object", HttpStatusCode.OK, body));
		using var http = new HttpClient(handler);
		await Assert.ThrowsAsync<InvalidDataException>(() => UpdateFeed.ReadAsync(http, UpdateChannel.Stable, CancellationToken.None));
		handler.AssertComplete();
	}

	[Fact]
	public async Task StableWithAMissingPublishedReleaseFailsLoudly() {
		using var handler = new FeedHandler(
			("/git/ref/tags/stable", HttpStatusCode.OK, """{"object":{"type":"tag","sha":"version-object"}}"""),
			("/git/tags/version-object", HttpStatusCode.OK, """{"tag":"v0.2.1"}"""),
			("/releases/tags/v0.2.1", HttpStatusCode.NotFound, "{}"));
		using var http = new HttpClient(handler);
		var error = await Assert.ThrowsAsync<HttpRequestException>(() => UpdateFeed.ReadAsync(http, UpdateChannel.Stable, CancellationToken.None));
		Assert.Equal(HttpStatusCode.NotFound, error.StatusCode);
		handler.AssertComplete();
	}

	[Theory]
	[InlineData(UpdateChannel.Stable, "/git/ref/tags/stable", HttpStatusCode.Unauthorized)]
	[InlineData(UpdateChannel.Latest, "/releases/tags/main-latest", HttpStatusCode.Forbidden)]
	public async Task AuthenticationAndRateLimitErrorsAreNotAnEmptyFeed(UpdateChannel channel, string path, HttpStatusCode status) {
		using var handler = new FeedHandler((path, status, "{}"));
		using var http = new HttpClient(handler);
		var error = await Assert.ThrowsAsync<HttpRequestException>(() => UpdateFeed.ReadAsync(http, channel, CancellationToken.None));
		Assert.Equal(status, error.StatusCode);
		handler.AssertComplete();
	}

	[Fact]
	public async Task DisabledUpdatesCannotQueryAFeed() {
		using var handler = new FeedHandler();
		using var http = new HttpClient(handler);
		await Assert.ThrowsAsync<InvalidOperationException>(() => UpdateFeed.ReadAsync(http, UpdateChannel.Off, CancellationToken.None));
		handler.AssertComplete();
	}

	private sealed class FeedHandler(params (string Path, HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler {
		private readonly Queue<(string Path, HttpStatusCode Status, string Body)> _responses = new(responses);

		public void AssertComplete() => Assert.Empty(_responses);

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
			Assert.NotEmpty(_responses);
			var response = _responses.Dequeue();
			Assert.Equal(HttpMethod.Get, request.Method);
			Assert.Equal($"https://api.github.com/repos/Kapps/weavie{response.Path}", request.RequestUri?.AbsoluteUri);
			return Task.FromResult(new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body) });
		}
	}
}
