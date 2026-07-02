using System.Text.Json;
using Weavie.Core.Sources;

namespace Weavie.Headless;

/// <summary>
/// A deterministic <see cref="ISourceConnector"/> for the headless integration harness / capture: <c>connect</c>
/// "validates" without a real API call (returning workspace "Demo Workspace"), and <c>fetch</c> serves one canned
/// <see cref="SourceDoc"/>. The source analogue of <see cref="FakePullRequests"/>; wired only when
/// <c>WEAVIE_FAKE_NOTION</c> points at a doc file, never in normal operation.
/// </summary>
internal sealed class FakeNotionSource : ISourceConnector {
	private readonly SourceDoc _doc;

	private FakeNotionSource(SourceDoc doc) {
		_doc = doc;
	}

	/// <summary>Reads a <c>{ "title": …, "markdown": …, "editedTime": … }</c> file into a connector that serves it as the fetched doc.</summary>
	public static FakeNotionSource FromFile(string path) {
		using var doc = JsonDocument.Parse(File.ReadAllText(path));
		var root = doc.RootElement;
		return new FakeNotionSource(new SourceDoc(Str(root, "title"), Str(root, "markdown"), Str(root, "editedTime")));
	}

	private static string Str(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

	public string? IdFor(string target) => NotionSource.ClaimsUrl(target) ? NotionSource.SourceId : null;

	// The demo connector always serves its canned doc, so it's always "connected" — an opened Notion URL fetches
	// straight through (the connect-prompt path is exercised by the connect-notion command, not open-target).
	public bool IsConnected(string target) => true;

	public string SetupUrlFor(string sourceId) => "https://app.notion.com/developers/tokens";

	// Accept a realistic-looking token (ntn_…), reject anything else — so a capture can show the inline-rejection path.
	public Task<string> SaveTokenAsync(string sourceId, string token, CancellationToken ct = default) =>
		token.StartsWith("ntn", StringComparison.Ordinal)
			? Task.FromResult("Demo Workspace")
			: Task.FromException<string>(new InvalidOperationException("Notion rejected the token — use a valid personal access token."));

	public Task<SourceDoc> FetchAsync(string target, CancellationToken ct = default) => Task.FromResult(_doc);
}
