using System.Net;
using System.Net.Http.Headers;

namespace Weavie.Hosting.Tests;

/// <summary>
/// A deterministic <see cref="HttpMessageHandler"/> for source tests: a test sets <see cref="Responder"/> to map a
/// request (by URL) to a canned status + JSON body, so the Notion token-validate (<c>/v1/users/me</c>) and API
/// calls never hit the network. Captures the requests it saw for assertions. Mirrors how the PR harness stubs its provider.
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler {
	private readonly List<HttpRequestMessage> _requests = [];

	/// <summary>Maps an incoming request to a (status, json-body) reply; defaults to 404 until a test sets it.</summary>
	public Func<HttpRequestMessage, (HttpStatusCode Status, string Json)> Responder { get; set; } =
		_ => (HttpStatusCode.NotFound, "{}");

	/// <summary>Every request this handler saw, in order.</summary>
	public IReadOnlyList<HttpRequestMessage> Requests => _requests;

	protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
		_requests.Add(request);
		var (status, json) = Responder(request);
		var response = new HttpResponseMessage(status) {
			Content = new StringContent(json),
		};
		response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		return Task.FromResult(response);
	}
}
