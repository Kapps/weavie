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
	/// Reads <paramref name="path"/> — <c>{ viewer, prs: [...], comments: [...] }</c> — into a static provider that
	/// serves the PR list and each PR's review comments (a comment's <c>number</c> names its PR) as <c>viewer</c>.
	/// </summary>
	public static StaticPullRequestProvider FromFile(string path) {
		using var doc = JsonDocument.Parse(File.ReadAllText(path));
		var root = doc.RootElement;
		var prsEl = root.GetProperty("prs");
		var provider = new StaticPullRequestProvider(
			ParsePrs(prsEl),
			ParseComments(root.TryGetProperty("comments", out var c) ? c : default),
			root.GetProperty("viewer").GetString() ?? throw new InvalidDataException($"{path}: 'viewer' must be a login."));
		SeedCommits(provider, prsEl);
		return provider;
	}

	// Each PR may list the commit shas it merged ("commits": ["abc…"]), so a blamed line resolves to its PR offline.
	private static void SeedCommits(StaticPullRequestProvider provider, JsonElement array) {
		if (array.ValueKind != JsonValueKind.Array) {
			return;
		}

		foreach (var pr in array.EnumerateArray()) {
			if (!pr.TryGetProperty("commits", out var commits) || commits.ValueKind != JsonValueKind.Array) {
				continue;
			}

			foreach (var sha in commits.EnumerateArray()) {
				if (sha.ValueKind == JsonValueKind.String && sha.GetString() is { Length: > 0 } value) {
					provider.PullRequestsByCommit[value] = Int(pr, "number");
				}
			}
		}
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
					HeadSha = Str(pr, "headSha"),
					BaseRef = Str(pr, "baseRef"),
					Url = Str(pr, "url"),
					IsDraft = pr.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
					State = ParseState(Str(pr, "state")),
				});
			}
		}

		return prs;
	}

	private static IReadOnlyList<(int Number, ReviewComment Comment)> ParseComments(JsonElement array) {
		var comments = new List<(int, ReviewComment)>();
		if (array.ValueKind == JsonValueKind.Array) {
			foreach (var cm in array.EnumerateArray()) {
				bool outdated = cm.TryGetProperty("outdated", out var o) && o.ValueKind == JsonValueKind.True;
				comments.Add((cm.GetProperty("number").GetInt32(), new ReviewComment {
					Id = Int(cm, "id"),
					Path = Str(cm, "path"),
					Line = outdated ? 0 : Int(cm, "line"),
					Outdated = outdated,
					Side = Str(cm, "side") is { Length: > 0 } side ? side : "right",
					Author = Str(cm, "author"),
					Body = Str(cm, "body"),
					CreatedAt = Str(cm, "createdAt"),
					UpdatedAt = Str(cm, "updatedAt") is { Length: > 0 } updated ? updated : Str(cm, "createdAt"),
					InReplyTo = Int(cm, "inReplyTo"),
				}));
			}
		}

		return comments;
	}

	private static string Str(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

	private static int Int(JsonElement element, string name) =>
		element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : 0;

	private static PullRequestState ParseState(string value) => value.ToLowerInvariant() switch {
		"merged" => PullRequestState.Merged,
		"closed" => PullRequestState.Closed,
		_ => PullRequestState.Open,
	};
}
