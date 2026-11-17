using Weavie.Core.Corrections;
using Weavie.Core.Editor;
using Weavie.Core.Suggestions;
using Weavie.Core.Workspaces;
using Weavie.Core.Worktrees;

namespace Weavie.Hosting;

/// <summary>
/// A git repository some session of the workspace lives in, or its plain home folder: the owner of everything a
/// git or repo-settings question is asked of. See docs/specs/multi-repo-workspaces.md.
/// </summary>
public sealed class Repo {
	/// <summary>The folder git and repo-scoped settings are asked of.</summary>
	public required string Root { get; init; }

	/// <summary>The key of the repo's on-disk state, the same in every workspace that reaches it.</summary>
	public WorkspaceId Id => WorkspaceId.ForPath(Root);

	/// <summary>The root's leaf name.</summary>
	public string Label => WorkspaceNaming.Label(Root);

	/// <summary>The repo's worktrees, or <c>null</c> when <see cref="Root"/> is not a git repository.</summary>
	public required WorktreeManager? Worktrees { get; init; }

	/// <summary>Runs the repo's worktree setup and teardown commands.</summary>
	public required IWorktreeProvisioner Provisioner { get; init; }

	/// <summary>The corrections every session of the repo records into.</summary>
	public required CorrectionCorpus Corrections { get; init; }

	/// <summary>Checkout-relative recent files, shared by the repo's sessions.</summary>
	public required RecentFilesStore RecentFiles { get; init; }

	/// <summary>The last file recorded as recent, so cursor moves within it bump frecency once.</summary>
	public string? LastRecentFile { get; set; }

	/// <summary>The repo's nudges, whose probe auto-configures its setup command and test profile.</summary>
	public required SuggestionService Suggestions { get; init; }

	/// <summary>The worktrees of a repo that must be a git repository to proceed.</summary>
	public WorktreeManager RequireWorktrees() => Worktrees
		?? throw new InvalidOperationException($"'{Label}' isn't a git repository, so worktree-backed sessions aren't available.");
}
