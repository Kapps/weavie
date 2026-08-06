using Weavie.Core.Git;
using Weavie.Core.Inference;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private async Task<BranchPreviewResult> PreviewBranchNameAsync(
		HostSession source,
		string? prompt,
		string agentProviderId,
		CancellationToken ct) {
		var taken = await TakenBranchNamesAsync(ct).ConfigureAwait(false);
		string fallback = UniqueDeterministicBranch(prompt, taken);
		if (string.IsNullOrWhiteSpace(prompt)) {
			return new BranchPreviewResult(fallback, false);
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
			return new BranchPreviewResult(fallback, false);
		}

		var result = await _inference.QueryAsync(
			agentProviderId,
			InferenceModelCategory.Utility,
			BranchNameInference.BuildPrompt(input),
			BranchNameInference.ResponseType,
			BranchNameInference.QueryOptions,
			ct).ConfigureAwait(false);
		if (result is InferenceFailure<BranchNameInferenceOutput> failure) {
			bool reportFailure = failure.Kind is not InferenceFailureKind.Disabled
				and not InferenceFailureKind.PolicyDenied;
			return new BranchPreviewResult(fallback, reportFailure);
		}

		if (result is not InferenceSuccess<BranchNameInferenceOutput> success) {
			throw new InvalidOperationException("Branch inference returned an unknown result type.");
		}

		string proposed = success.Value.Branch.Trim();
		if (proposed.Length == 0 || taken.Contains(proposed)) {
			return new BranchPreviewResult(fallback, true);
		}

		try {
			if (!await git.IsValidBranchNameAsync(source.WorkspaceRoot, proposed, ct).ConfigureAwait(false)) {
				return new BranchPreviewResult(fallback, true);
			}

			return await git.BranchExistsAsync(source.WorkspaceRoot, proposed, ct).ConfigureAwait(false)
				? new BranchPreviewResult(fallback, true)
				: new BranchPreviewResult(proposed, false);
		} catch (GitException) {
			return new BranchPreviewResult(fallback, true);
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
