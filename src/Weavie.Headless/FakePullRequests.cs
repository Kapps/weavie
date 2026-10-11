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
	/// Reads <paramref name="path"/> — <c>{ viewer, viewerAvatarUrl, prs: [...], threads: [...], pendingReviews: [...] }</c> — into
	/// a static provider that serves the PR list, each PR's review threads, and the viewer's pending reviews (each
	/// thread's and review's <c>number</c> names its PR) as <c>viewer</c>.
	/// </summary>
	public static StaticPullRequestProvider FromFile(string path) {
		using var doc = JsonDocument.Parse(File.ReadAllText(path));
		var root = doc.RootElement;
		var prsEl = root.GetProperty("prs");
		var provider = new StaticPullRequestProvider(
			ParsePrs(prsEl),
			ParseThreads(root.TryGetProperty("threads", out var t) ? t : default),
			new ForgeUser(
				root.GetProperty("viewer").GetString() ?? throw new InvalidDataException($"{path}: 'viewer' must be a login."),
				Str(root, "viewerAvatarUrl")));
		SeedCommits(provider, prsEl);
		SeedDetails(provider, prsEl);
		if (root.TryGetProperty("pendingReviews", out var pending)) {
			foreach (var review in pending.EnumerateArray()) {
				provider.PendingReviews[Int(review, "number")] = new PendingReview(Str(review, "id"), Str(review, "body"));
			}
		}

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

	// Each PR may carry its overview ("overview": { body, reviewers, conversation, checks }); states use the wire names.
	private static void SeedDetails(StaticPullRequestProvider provider, JsonElement array) {
		foreach (var pr in array.EnumerateArray()) {
			if (!pr.TryGetProperty("overview", out var overview)) {
				continue;
			}

			int number = Int(pr, "number");
			provider.Details[number] = new PullRequestDetail(
				$"PR_{number}",
				number,
				Str(pr, "url"),
				Str(pr, "title"),
				Str(overview, "body"),
				pr.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
				new ForgeUser(Str(pr, "author"), string.Empty),
				Str(overview, "createdAt"),
				[.. Items(overview, "reviewers").Select(r => new PullRequestReviewer(
					new ForgeUser(Str(r, "login"), string.Empty), Enum.Parse<ReviewerState>(Str(r, "state"), ignoreCase: true)))],
				[.. Items(overview, "conversation").Select(c => new PullRequestConversationItem(
					Str(c, "id"),
					new ForgeUser(Str(c, "author"), string.Empty),
					Str(c, "body"),
					Str(c, "review") is { Length: > 0 } review ? Enum.Parse<ReviewerState>(review, ignoreCase: true) : null,
					Str(c, "createdAt"),
					string.Empty))],
				[.. Items(overview, "checks").Select(c => new PullRequestCheck(
					Str(c, "name"), Enum.Parse<CheckState>(Str(c, "state"), ignoreCase: true), Str(c, "url")))]);
		}
	}

	private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
		element.TryGetProperty(name, out var array) ? array.EnumerateArray() : [];

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
					BaseSha = Str(pr, "baseSha"),
					Url = Str(pr, "url"),
					IsDraft = pr.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True,
					State = ParseState(Str(pr, "state")),
				});
			}
		}

		return prs;
	}

	private static IReadOnlyList<(int Number, ReviewThread Thread)> ParseThreads(JsonElement array) {
		var threads = new List<(int, ReviewThread)>();
		if (array.ValueKind == JsonValueKind.Array) {
			foreach (var th in array.EnumerateArray()) {
				bool outdated = th.TryGetProperty("outdated", out var o) && o.ValueKind == JsonValueKind.True;
				threads.Add((th.GetProperty("number").GetInt32(), new ReviewThread(
					Str(th, "id"),
					Str(th, "path"),
					outdated ? 0 : Int(th, "line"),
					Str(th, "side") is { Length: > 0 } side ? side : "right",
					outdated,
					[.. th.GetProperty("comments").EnumerateArray().Select(cm => new ReviewComment {
						Id = Str(cm, "id"),
						Author = Str(cm, "author"),
						AuthorAvatarUrl = Str(cm, "avatarUrl"),
						Url = Str(cm, "url"),
						Body = Str(cm, "body"),
						CreatedAt = Str(cm, "createdAt"),
						UpdatedAt = Str(cm, "updatedAt") is { Length: > 0 } updated ? updated : Str(cm, "createdAt"),
						Pending = cm.TryGetProperty("pending", out var p) && p.ValueKind == JsonValueKind.True,
						Mine = false, // The provider derives it from the viewer.
					})])));
			}
		}

		return threads;
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
