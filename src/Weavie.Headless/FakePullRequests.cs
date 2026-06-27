using System.Text.Json;
using Weavie.Core.Review;

namespace Weavie.Headless;

/// <summary>
/// Loads canned pull requests (and review comments) from a JSON file into a <see cref="StaticPullRequestProvider"/>,
/// so the headless host can serve a deterministic Open-PR journey offline (the PR analogue of the fake-claude
/// script). Wired only when <c>WEAVIE_FAKE_PRS</c> points at a file; never used in normal operation.
/// </summary>
internal static class FakePullRequests {
	/// <summary>
	/// Reads <paramref name="path"/> — a JSON array of PRs, or an object <c>{ prs: [...], comments: [...] }</c> —
	/// into a static provider that serves both the PR list and its review comments.
	/// </summary>
	public static StaticPullRequestProvider FromFile(string path) {
		using var doc = JsonDocument.Parse(File.ReadAllText(path));
		var root = doc.RootElement;
		var prsEl = root.ValueKind == JsonValueKind.Array ? root
			: root.TryGetProperty("prs", out var p) ? p : default;
		var commentsEl = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("comments", out var c) ? c : default;
		return new StaticPullRequestProvider(ParsePrs(prsEl), ParseComments(commentsEl));
	}

	private static IReadOnlyList<PullRequestSummary> ParsePrs(JsonElement array) {
		var prs = new List<PullRequestSummary>();
		if (array.ValueKind == JsonValueKind.Array) {
			foreach (var pr in array.EnumerateArray()) {
				prs.Add(new PullRequestSummary {
					Number = Int(pr, "number"),
					Title = Str(pr, "title"),
					Author = Str(pr, "author"),
					HeadRef = Str(pr, "headRef"),
					BaseRef = Str(pr, "baseRef"),
					Url = Str(pr, "url"),
					IsDraft = pr.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
				});
			}
		}

		return prs;
	}

	private static IReadOnlyList<ReviewComment> ParseComments(JsonElement array) {
		var comments = new List<ReviewComment>();
		if (array.ValueKind == JsonValueKind.Array) {
			foreach (var cm in array.EnumerateArray()) {
				comments.Add(new ReviewComment {
					Id = Int(cm, "id"),
					Path = Str(cm, "path"),
					Line = Int(cm, "line"),
					Side = cm.TryGetProperty("side", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "right" : "right",
					Author = Str(cm, "author"),
					Body = Str(cm, "body"),
					CreatedAt = Str(cm, "createdAt"),
					InReplyTo = Int(cm, "inReplyTo"),
				});
			}
		}

		return comments;
	}

	private static string Str(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

	private static int Int(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;
}
