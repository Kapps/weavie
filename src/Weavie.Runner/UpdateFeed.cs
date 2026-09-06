using System.Net;
using System.Text.Json;

namespace Weavie.Runner;

internal static class UpdateFeed {
	private const string RepositoryApi = "https://api.github.com/repos/Kapps/weavie";

	internal static async Task<JsonDocument?> ReadAsync(HttpClient http, UpdateChannel channel, CancellationToken ct) {
		if (channel is not (UpdateChannel.Stable or UpdateChannel.Latest)) {
			throw new InvalidOperationException("An update feed requires stable or latest.");
		}

		try {
			return await ReadReleaseAsync(http, channel, ct).ConfigureAwait(false);
		} catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException) {
			throw new InvalidDataException($"Malformed {channel.ToString().ToLowerInvariant()} release feed.", ex);
		}
	}

	private static async Task<JsonDocument?> ReadReleaseAsync(HttpClient http, UpdateChannel channel, CancellationToken ct) {
		string releaseTag;
		switch (channel) {
			case UpdateChannel.Latest:
				releaseTag = "main-latest";
				break;
			case UpdateChannel.Stable:
				using (var response = await http.GetAsync($"{RepositoryApi}/git/ref/tags/stable", ct).ConfigureAwait(false)) {
					if (response.StatusCode == HttpStatusCode.NotFound) {
						return null;
					}

					response.EnsureSuccessStatusCode();
					using var reference = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
					var target = reference.RootElement.GetProperty("object");
					if (target.GetProperty("type").GetString() != "tag") {
						throw new InvalidDataException("stable must point to an annotated version tag.");
					}

					string sha = target.GetProperty("sha").GetString()
						?? throw new InvalidDataException("stable has no tag object SHA.");
					using var tagResponse = await http.GetAsync($"{RepositoryApi}/git/tags/{Uri.EscapeDataString(sha)}", ct).ConfigureAwait(false);
					tagResponse.EnsureSuccessStatusCode();
					using var tag = JsonDocument.Parse(await tagResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
					releaseTag = tag.RootElement.GetProperty("tag").GetString() ?? string.Empty;
					if (!releaseTag.StartsWith('v') || !Version.TryParse(releaseTag[1..], out var version)
						|| version.Build < 0 || version.Revision >= 0 || releaseTag != $"v{version}") {
						throw new InvalidDataException($"stable points to invalid release tag '{releaseTag}'.");
					}
				}
				break;
			default:
				throw new InvalidOperationException("An update feed requires stable or latest.");
		}

		using var releaseResponse = await http.GetAsync($"{RepositoryApi}/releases/tags/{releaseTag}", ct).ConfigureAwait(false);
		if (channel == UpdateChannel.Latest && releaseResponse.StatusCode == HttpStatusCode.NotFound) {
			return null;
		}

		releaseResponse.EnsureSuccessStatusCode();
		return JsonDocument.Parse(await releaseResponse.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
	}
}
