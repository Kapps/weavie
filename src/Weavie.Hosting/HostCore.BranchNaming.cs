using Weavie.Core.Git;
using Weavie.Core.Inference;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private async Task<string> PreviewBranchNameAsync(
		HostSession source,
		string? prompt,
		string agentProviderId,
		CancellationToken ct) {
		var taken = await TakenBranchNamesAsync(ct).ConfigureAwait(false);
		string fallback = UniqueDeterministicBranch(prompt, taken);
		if (string.IsNullOrWhiteSpace(prompt)) {
			return fallback;
		}

		var git = new GitService();
		BranchNameInferenceInput input;
		try {
			input = new BranchNameInferenceInput {
				Prompt = prompt.Trim(),
				CurrentBranch = await git.GetCurrentBranchAsync(source.WorkspaceRoot, ct).ConfigureAwait(false) ?? string.Empty,
				RecentBranches = await git.ListRecentBranchesAsync(source.WorkspaceRoot, 20, ct).ConfigureAwait(false),
			};
		} catch (GitException) {
			return fallback;
		}

		var result = await _inference.QueryAsync(
			agentProviderId,
			InferenceModelCategory.Utility,
			BranchNameInference.BuildPrompt(input),
			BranchNameInference.ResponseType,
			BranchNameInference.QueryOptions,
			ct).ConfigureAwait(false);
		if (result is not InferenceSuccess<BranchNameInferenceOutput> success) {
			return fallback;
		}

		string proposed = success.Value.Branch.Trim();
		if (proposed.Length == 0 || taken.Contains(proposed)) {
			return fallback;
		}

		try {
			if (!await git.IsValidBranchNameAsync(source.WorkspaceRoot, proposed, ct).ConfigureAwait(false)) {
				return fallback;
			}

			return await git.BranchExistsAsync(source.WorkspaceRoot, proposed, ct).ConfigureAwait(false)
				? fallback
				: proposed;
		} catch (GitException) {
			return fallback;
		}
	}

	private async Task<string> DeriveUniqueDeterministicBranchNameAsync(string? prompt, CancellationToken ct) =>
		UniqueDeterministicBranch(prompt, await TakenBranchNamesAsync(ct).ConfigureAwait(false));

	private async Task<HashSet<string>> TakenBranchNamesAsync(CancellationToken ct) {
		var taken = new HashSet<string>(StringComparer.Ordinal);
		if (_sessions is not null) {
			foreach (var slot in _sessions.Slots) {
				taken.Add(slot.Label);
			}
		}

		foreach (string branch in await new GitService()
			.ListBranchesAsync(WorkspaceRoot, ct)
			.ConfigureAwait(false)) {
			taken.Add(branch);
		}

		return taken;
	}

	private static string UniqueDeterministicBranch(string? prompt, IReadOnlySet<string> taken) {
		string slug = "session";
		if (!string.IsNullOrWhiteSpace(prompt)) {
			char[] chars = [.. prompt.Trim().ToLowerInvariant().Take(40).Select(c => char.IsLetterOrDigit(c) ? c : '-')];
			slug = new string(chars).Trim('-');
			if (slug.Length == 0) {
				slug = "session";
			}
		}

		string candidate = slug;
		int n = 2;
		while (taken.Contains(candidate)) {
			candidate = $"{slug}-{n}";
			n++;
		}

		return candidate;
	}
}
