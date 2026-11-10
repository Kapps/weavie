using System.Net;
using System.Net.Http.Headers;

namespace Weavie.TestSupport;

/// <summary>
/// A deterministic <see cref="HttpMessageHandler"/>: <see cref="Responder"/> maps each request to a canned status and
/// JSON body (or throws, as a transport failure would), so no test reaches the network. Records every request and
/// its body, read before the caller disposes the content.
/// </summary>
public sealed class RecordingHttpHandler : HttpMessageHandler {
	private readonly List<HttpRequestMessage> _requests = [];
	private readonly List<string> _bodies = [];

	/// <summary>Maps an incoming request to a (status, json-body) reply; 404 until a test sets it.</summary>
	public Func<HttpRequestMessage, (HttpStatusCode Status, string Json)> Responder { get; set; } =
		_ => (HttpStatusCode.NotFound, "{}");

	/// <summary>Every request this handler saw, in order.</summary>
	public IReadOnlyList<HttpRequestMessage> Requests => _requests;

	/// <summary>Each request's body (empty without content), parallel to <see cref="Requests"/>.</summary>
	public IReadOnlyList<string> Bodies => _bodies;

	/// <inheritdoc/>
	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
		_bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
		_requests.Add(request);
		var (status, json) = Responder(request);
		var response = new HttpResponseMessage(status) {
			Content = new StringContent(json),
		};
		response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
		return response;
	}
}
