using Weavie.Core.Commands;

namespace Weavie.Core.Sessions;

/// <summary>An encoded image included in the first input for a newly-created session.</summary>
public sealed record NewSessionAttachment {
	/// <summary>The client-generated attachment id, unique within the request.</summary>
	public required string Id { get; init; }

	/// <summary>The image MIME type.</summary>
	public required string Mime { get; init; }

	/// <summary>The encoded image bytes as base64.</summary>
	public required string DataB64 { get; init; }
}

/// <summary>Arguments for creating or attaching a session.</summary>
public sealed record NewSessionRequest {
	/// <summary>The required branch (and worktree) name.</summary>
	public string? Branch { get; init; }

	/// <summary>The base to branch from: <c>"main"</c> (the repository's default branch) or <c>"source"</c> (the invoking session's HEAD); <c>null</c> means main. Ignored when <see cref="Existing"/> is set.</summary>
	public string? Base { get; init; }

	/// <summary>Optional text in the new session's first agent input.</summary>
	public string? Prompt { get; init; }

	/// <summary>Images submitted atomically with <see cref="Prompt"/> as the new session's first input.</summary>
	public IReadOnlyList<NewSessionAttachment> Attachments { get; init; } = [];

	/// <summary>The provider for this new session; <c>null</c> means the host's default provider setting.</summary>
	public string? AgentProviderId { get; init; }

	/// <summary>
	/// When true, <see cref="Branch"/> names an <em>existing</em> branch to check out into a new worktree
	/// (no new branch, no base); if a session already exists for it, the host switches to that instead.
	/// </summary>
	public bool Existing { get; init; }
}

/// <summary>Arguments for forking the invoking session into a new worktree off its HEAD.</summary>
public sealed record ForkSessionRequest {
	/// <summary>The required new branch (and worktree) name.</summary>
	public string? Branch { get; init; }

	/// <summary>The handoff brief seeded as the fork's first prompt (the forking Claude's own summary).</summary>
	public string? Handoff { get; init; }
}

/// <summary>
/// The host-side operations behind Weavie's session commands — create/fork/close spawn or tear down a
/// session's native window backend, so they're implemented per host and invoked through this seam.
/// </summary>
public interface ISessionHost {
	/// <summary>Creates a new session on its own worktree + branch, optionally seeding its first agent input.</summary>
	Task<CommandResult> NewSessionAsync(NewSessionRequest request, CancellationToken ct = default);

	/// <summary>Forks the invoking session into a new worktree off its HEAD, carrying a handoff brief.</summary>
	Task<CommandResult> ForkSessionAsync(ForkSessionRequest request, CancellationToken ct = default);

	/// <summary>Loads a dormant session's backend (by <paramref name="sessionId"/>) in the background, without switching to it.</summary>
	Task<CommandResult> LoadSessionAsync(string? sessionId, CancellationToken ct = default);

	/// <summary>Unloads the invoking session, or the given <paramref name="sessionId"/>, into a dormant chip while keeping its worktree.</summary>
	Task<CommandResult> UnloadSessionAsync(
		string? sessionId,
		CommandInvocationContext context,
		CancellationToken ct = default);

	/// <summary>
	/// Deletes the session named by the required <paramref name="sessionId"/>: removes its git worktree but keeps
	/// the branch. Refuses when the worktree has uncommitted changes unless <paramref name="force"/>. A blank id is
	/// rejected — it must never fall back to the focused session, which may not be the caller's own (issue #217).
	/// </summary>
	Task<CommandResult> DeleteSessionAsync(
		string? sessionId,
		bool force,
		CommandInvocationContext context,
		CancellationToken ct = default);

	/// <summary>
	/// Classifies a session's worktree for the delete confirm without deleting anything: the result's
	/// <see cref="CommandResult.DataJson"/> carries <c>{ state, label }</c> where <c>state</c> is
	/// <c>clean</c>/<c>untracked</c>/<c>modified</c>, so the UI can escalate the confirmation. The interactive
	/// delete classifies first, then deletes with <c>force</c> on confirm.
	/// </summary>
	Task<CommandResult> ClassifyDeleteAsync(string? sessionId, CancellationToken ct = default);
}
