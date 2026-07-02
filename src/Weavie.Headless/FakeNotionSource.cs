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
	private SourceDoc _doc; // mutable: UpdateAsync applies edits in-memory so a save round-trips like the real API
	private readonly bool _rejectEdits;

	private FakeNotionSource(SourceDoc doc, bool rejectEdits) {
		_doc = doc;
		_rejectEdits = rejectEdits;
	}

	/// <summary>Reads a <c>{ "title": …, "markdown": …, "editedTime": …, "truncated": …, "rejectEdits": … }</c>
	/// file into a connector that serves it as the fetched doc (<c>rejectEdits</c> makes every update conflict,
	/// for driving the stale-edit UX).</summary>
	public static FakeNotionSource FromFile(string path) {
		using var doc = JsonDocument.Parse(File.ReadAllText(path));
		var root = doc.RootElement;
		bool truncated = root.TryGetProperty("truncated", out var t) && t.ValueKind == JsonValueKind.True;
		bool rejectEdits = root.TryGetProperty("rejectEdits", out var r) && r.ValueKind == JsonValueKind.True;
		return new FakeNotionSource(new SourceDoc(Str(root, "title"), Str(root, "markdown"), Str(root, "editedTime"), truncated, 0), rejectEdits);
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

	// Enforces update_content's real contract — the op must match the document exactly once — and applies it to
	// the in-memory doc, so the e2e can prove the web's uniqueness expansion against duplicated content.
	public Task<SourceDoc> UpdateAsync(string target, string oldStr, string newStr, CancellationToken ct = default) {
		int first = _doc.Markdown.IndexOf(oldStr, StringComparison.Ordinal);
		bool matchesOnce = first >= 0 && _doc.Markdown.IndexOf(oldStr, first + 1, StringComparison.Ordinal) < 0;
		if (_rejectEdits || !matchesOnce) {
			return Task.FromException<SourceDoc>(new SourceConflictException("The page changed in Notion since it was fetched."));
		}

		_doc = _doc with { Markdown = string.Concat(_doc.Markdown.AsSpan(0, first), newStr, _doc.Markdown.AsSpan(first + oldStr.Length)) };
		return Task.FromResult(_doc);
	}
}
