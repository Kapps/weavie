using System.Text.Json;
using Weavie.Core.Review;

namespace Weavie.Headless;

/// <summary>
/// Loads canned pull requests from a JSON file into a <see cref="StaticPullRequestProvider"/>, so the headless
/// host can serve a deterministic Open-PR journey offline (the PR analogue of the fake-claude script). Wired only
/// when <c>WEAVIE_FAKE_PRS</c> points at a file; never used in normal operation.
/// </summary>
internal static class FakePullRequests {
	/// <summary>Reads <paramref name="path"/> — a JSON array of <c>{ number, title, author, headRef, url, draft }</c> — into a static provider.</summary>
	public static IPullRequestProvider FromFile(string path) {
		using var doc = JsonDocument.Parse(File.ReadAllText(path));
		var prs = new List<PullRequestSummary>();
		if (doc.RootElement.ValueKind == JsonValueKind.Array) {
			foreach (var pr in doc.RootElement.EnumerateArray()) {
				prs.Add(new PullRequestSummary {
					Number = pr.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0,
					Title = Str(pr, "title"),
					Author = Str(pr, "author"),
					HeadRef = Str(pr, "headRef"),
					BaseRef = Str(pr, "baseRef"),
					Url = Str(pr, "url"),
					IsDraft = pr.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
				});
			}
		}

		return new StaticPullRequestProvider(prs);
	}

	private static string Str(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString() ?? string.Empty
			: string.Empty;
}
