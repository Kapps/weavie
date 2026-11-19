using System.Net;

namespace Weavie.TestSupport;

/// <summary>
/// The upstream spelling-dictionary host, answering every locale with a two-word dictionary (<c>colour</c>,
/// <c>école</c>) and the catalogue with <c>en</c> and <c>fr</c>, so no test reaches the network.
/// </summary>
public sealed class FakeDictionaryServer : HttpMessageHandler {
	/// <summary>The path of every request, in order.</summary>
	public List<string> Requests { get; } = [];

	/// <summary>Runs before each request is answered; throw to fail it as a transport would.</summary>
	public Action OnRequest { get; set; } = () => { };

	/// <summary>Every request waits for this before it is answered.</summary>
	public Task Gate { get; set; } = Task.CompletedTask;

	/// <summary>The dictionary file that fails: unavailable, or empty when <see cref="Empty"/>.</summary>
	public string FailedFile { get; set; } = "";

	/// <summary>Whether <see cref="FailedFile"/> downloads as an empty dictionary.</summary>
	public bool Empty { get; init; }

	/// <inheritdoc/>
	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
		OnRequest();
		string path = request.RequestUri!.AbsolutePath;
		Requests.Add(path);
		await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		string file = Path.GetFileName(path);
		bool failed = file == FailedFile;
		string content = file switch {
			"index.aff" => "SET UTF-8\nTRY abcdefghijklmnopqrstuvwxyz\n",
			"index.dic" => failed && Empty ? "0\n" : "2\ncolour\nécole\n",
			"license" => "test license",
			_ => "[{\"name\":\"en\"},{\"name\":\"fr\"}]",
		};
		return new HttpResponseMessage(failed && !Empty ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(content) };
	}
}
