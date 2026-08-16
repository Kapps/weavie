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
			return new BranchPreviewResult(string.Empty, "Type a prompt before requesting a branch suggestion.");
		}

		var git = new GitService();
		HashSet<string> taken;
		BranchNameInferenceInput input;
		try {
			taken = await TakenBranchNamesAsync(ct).ConfigureAwait(false);
			input = new BranchNameInferenceInput {
				Prompt = prompt.Trim(),
				CurrentBranch = await git.GetCurrentBranchAsync(sourceRoot, ct).ConfigureAwait(false) ?? string.Empty,
				RecentBranches = await git.ListRecentBranchesAsync(sourceRoot, 20, ct).ConfigureAwait(false),
			};
		} catch (GitException ex) {
			return new BranchPreviewResult(string.Empty, $"Couldn't read repository branch information: {ex.Message}");
		}

		// The branch is named before its session exists, so the request is owned by the workspace it forks from.
		var owner = new InferenceOwner { AgentProviderId = agentProviderId, Workspace = sourceRoot };
		var result = await _inference.QueryAsync(
			owner,
			InferenceModelCategory.Utility,
			BranchNameInference.BuildPrompt(input),
			BranchNameInference.ResponseType,
			BranchNameInference.QueryOptions,
			ct).ConfigureAwait(false);
		if (result is InferenceFailure<BranchNameInferenceOutput> failure) {
			return new BranchPreviewResult(string.Empty, failure.Detail);
		}

		if (result is not InferenceSuccess<BranchNameInferenceOutput> success) {
			throw new InvalidOperationException("Branch inference returned an unknown result type.");
		}

		string proposed = success.Value.Branch.Trim();
		if (proposed.Length == 0) {
			return new BranchPreviewResult(string.Empty, "The inference provider returned an empty branch name.");
		}
		if (taken.Contains(proposed)) {
			return new BranchPreviewResult(string.Empty, "The suggested branch name is already in use.");
		}

		try {
			if (!await git.IsValidBranchNameAsync(sourceRoot, proposed, ct).ConfigureAwait(false)) {
				return new BranchPreviewResult(string.Empty, "The suggested branch name isn't valid.");
			}

			return await git.BranchExistsAsync(sourceRoot, proposed, ct).ConfigureAwait(false)
				? new BranchPreviewResult(string.Empty, "The suggested branch name already exists.")
				: new BranchPreviewResult(proposed, null);
		} catch (GitException ex) {
			return new BranchPreviewResult(string.Empty, $"Couldn't validate the suggested branch name: {ex.Message}");
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
