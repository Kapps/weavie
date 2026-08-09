using Weavie.Core.Git;
using Weavie.Core.Inference;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private async Task<BranchPreviewResult> PreviewBranchNameAsync(
		string sourceRoot,
		string? prompt,
		string agentProviderId,
		CancellationToken ct) {
		if (string.IsNullOrWhiteSpace(prompt)) {
			return new BranchPreviewResult(string.Empty, true);
		}
		var taken = await TakenBranchNamesAsync(ct).ConfigureAwait(false);

		var git = new GitService();
		BranchNameInferenceInput input;
		try {
			input = new BranchNameInferenceInput {
				Prompt = prompt.Trim(),
				CurrentBranch = await git.GetCurrentBranchAsync(sourceRoot, ct).ConfigureAwait(false) ?? string.Empty,
				RecentBranches = await git.ListRecentBranchesAsync(sourceRoot, 20, ct).ConfigureAwait(false),
			};
		} catch (GitException) {
			return new BranchPreviewResult(string.Empty, true);
		}

		var result = await _inference.QueryAsync(
			agentProviderId,
			InferenceModelCategory.Utility,
			BranchNameInference.BuildPrompt(input),
			BranchNameInference.ResponseType,
			BranchNameInference.QueryOptions,
			ct).ConfigureAwait(false);
		if (result is InferenceFailure<BranchNameInferenceOutput>) {
			return new BranchPreviewResult(string.Empty, true);
		}

		if (result is not InferenceSuccess<BranchNameInferenceOutput> success) {
			throw new InvalidOperationException("Branch inference returned an unknown result type.");
		}

		string proposed = success.Value.Branch.Trim();
		if (proposed.Length == 0 || taken.Contains(proposed)) {
			return new BranchPreviewResult(string.Empty, true);
		}

		try {
			if (!await git.IsValidBranchNameAsync(sourceRoot, proposed, ct).ConfigureAwait(false)) {
				return new BranchPreviewResult(string.Empty, true);
			}

			return await git.BranchExistsAsync(sourceRoot, proposed, ct).ConfigureAwait(false)
				? new BranchPreviewResult(string.Empty, true)
				: new BranchPreviewResult(proposed, false);
		} catch (GitException) {
			return new BranchPreviewResult(string.Empty, true);
		}
	}

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
}
