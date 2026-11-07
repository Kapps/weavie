using System.Net;
using System.Net.Http.Headers;
using Weavie.Core.Review;
using Xunit;

namespace Weavie.Core.Tests;

/// <summary>Attachment kinds come from the content type GitHub serves, fetched one byte at a time.</summary>
public sealed partial class GitHubReviewCommentsTests {
	private static readonly Uri Attachment = new("https://github.com/user-attachments/assets/f6db4567-e7b9-408c-a0a1-9a3932911750");

	[Theory]
	[InlineData(HttpStatusCode.PartialContent, "video/mp4", AttachmentKind.Video)]
	[InlineData(HttpStatusCode.OK, "image/png", AttachmentKind.Image)]
	[InlineData(HttpStatusCode.OK, "text/html", AttachmentKind.Unsupported)]
	[InlineData(HttpStatusCode.NotFound, "image/png", AttachmentKind.Unsupported)]
	public async Task AttachmentKindAsync_ReadsTheServedContentType(HttpStatusCode status, string type, AttachmentKind expected) {
		var handler = new ContentTypeHandler(status, type);
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());

		Assert.Equal(expected, await provider.AttachmentKindAsync(Attachment));
		Assert.Equal("bytes=0-0", handler.Range);
		Assert.Null(handler.Authorization);
	}

	[Theory]
	[InlineData("https://example.com/user-attachments/assets/f6db4567-e7b9-408c-a0a1-9a3932911750")]
	[InlineData("http://github.com/user-attachments/assets/f6db4567-e7b9-408c-a0a1-9a3932911750")]
	[InlineData("https://github.com:8443/user-attachments/assets/f6db4567-e7b9-408c-a0a1-9a3932911750")]
	[InlineData("https://user@github.com/user-attachments/assets/f6db4567-e7b9-408c-a0a1-9a3932911750")]
	public async Task AttachmentKindAsync_FetchesOnlyFromGitHub(string url) {
		var handler = new ContentTypeHandler(HttpStatusCode.OK, "image/png");
		var provider = new GitHubReviewProvider(new HttpClient(handler), new TokenSource());

		await Assert.ThrowsAsync<ArgumentException>(() => provider.AttachmentKindAsync(new Uri(url)));
		Assert.Null(handler.Range);
	}

	private sealed class ContentTypeHandler(HttpStatusCode status, string type) : HttpMessageHandler {
		public string? Range { get; private set; }

		public AuthenticationHeaderValue? Authorization { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
			Range = request.Headers.Range?.ToString();
			Authorization = request.Headers.Authorization;
			var content = new ByteArrayContent([0]);
			content.Headers.ContentType = new MediaTypeHeaderValue(type);
			return Task.FromResult(new HttpResponseMessage(status) { Content = content });
		}
	}
}
