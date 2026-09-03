using Weavie.Core.Editor;
using Weavie.Core.Git;
using Weavie.Core.Inference;
using Weavie.Core.Sessions;

namespace Weavie.Hosting;

public sealed partial class HostCore {
	private async Task<BranchPreviewResult> PreviewBranchNameAsync(
		string sourceRoot,
		string? prompt,
		IReadOnlyList<NewSessionAttachment> attachments,
		InferenceInvocationOrigin origin,
		CancellationToken ct) {
		if (!ValidateBranchPreviewAttachments(attachments, out string attachmentError)) {
			return BranchPreviewResult.Failed(attachmentError);
		}
		if (!TryDecodeInitialInput(prompt, attachments, out var initialInput, out string inputError)) {
			return BranchPreviewResult.Failed(inputError);
		}
		if (initialInput is null) {
			return BranchPreviewResult.Failed(
				"Type a prompt or attach an image before requesting a branch suggestion.");
		}

		var git = new GitService();
		HashSet<string> taken;
		BranchNameInferenceInput input;
		try {
			taken = await TakenBranchNamesAsync(ct).ConfigureAwait(false);
			var recent = await git.ListRecentBranchesAsync(sourceRoot, 20, ct).ConfigureAwait(false);
			string? defaultBranch = await git.ResolveDefaultBranchAsync(sourceRoot, ct).ConfigureAwait(false);
			var mine = NamingExamples(recent.Mine, defaultBranch);
			input = new BranchNameInferenceInput {
				Prompt = initialInput.Text,
				CurrentBranch = await git.GetCurrentBranchAsync(sourceRoot, ct).ConfigureAwait(false) ?? string.Empty,
				AuthorEmail = recent.AuthorEmail,
				MyRecentBranches = mine,
				// The user's own conventions lead, so a team's branches inform the name only when they have none.
				OtherRecentBranches = mine.Count > 0 ? [] : NamingExamples(recent.Others, defaultBranch),
			};
		} catch (GitException ex) {
			return BranchPreviewResult.Failed($"Couldn't read repository branch information: {ex.Message}");
		}

		// The branch is named before its session exists, so the request is owned by the workspace it forks from.
		var owner = new InferenceOwner { Workspace = sourceRoot };
		var result = await _inference.QueryAsync(
			owner,
			InferenceModelCategory.Utility,
			new InferenceInput {
				Prompt = BranchNameInference.BuildPrompt(input),
				Images = [.. initialInput.Attachments.Select(image => new InferenceInputImage {
					Mime = image.Mime,
					Bytes = image.Bytes,
				})],
			},
			BranchNameInference.ResponseType,
			BranchNameInference.QueryOptions with { Origin = origin },
			ct).ConfigureAwait(false);
		if (result is InferenceFailure<BranchNameInferenceOutput> failure) {
			return BranchPreviewResult.Failed(failure.Detail);
		}

		if (result is not InferenceSuccess<BranchNameInferenceOutput> success) {
			throw new InvalidOperationException("Branch inference returned an unknown result type.");
		}

		if (success.Value.NeedsMoreDetail) {
			return BranchPreviewResult.MoreDetail;
		}

		string proposed = success.Value.Branch.Trim();
		if (proposed.Length == 0) {
			return BranchPreviewResult.Failed("The inference provider returned an empty branch name.");
		}
		if (taken.Contains(proposed)) {
			return BranchPreviewResult.Failed("The suggested branch name is already in use.");
		}

		try {
			if (!await git.IsValidBranchNameAsync(sourceRoot, proposed, ct).ConfigureAwait(false)) {
				return BranchPreviewResult.Failed("The suggested branch name isn't valid.");
			}

			return await git.BranchExistsAsync(sourceRoot, proposed, ct).ConfigureAwait(false)
				? BranchPreviewResult.Failed("The suggested branch name already exists.")
				: BranchPreviewResult.Named(proposed);
		} catch (GitException ex) {
			return BranchPreviewResult.Failed($"Couldn't validate the suggested branch name: {ex.Message}");
		}
	}

	// The default branch is nobody's naming example: it teaches no convention, so authoring its tip is not the user
	// having branches of their own.
	private static IReadOnlyList<string> NamingExamples(IReadOnlyList<string> branches, string? defaultBranch) =>
		[.. branches.Where(branch => branch != defaultBranch)];

	private static bool ValidateBranchPreviewAttachments(
		IReadOnlyList<NewSessionAttachment> attachments,
		out string error) {
		var options = BranchNameInference.QueryOptions;
		if (attachments.Count > options.MaxImageCount) {
			error = $"Branch suggestions accept up to {options.MaxImageCount} images.";
			return false;
		}

		long imageBytes = 0;
		foreach (var attachment in attachments) {
			long upperBound = PastedImageMedia.DecodedByteUpperBound(attachment.DataB64);
			if (upperBound > options.MaxImageBytes - imageBytes) {
				error = $"Branch-suggestion images can total up to {options.MaxImageBytes / (1024 * 1024)} MB.";
				return false;
			}
			imageBytes += upperBound;
		}

		error = string.Empty;
		return true;
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
