using Weavie.Core.Commands;

namespace Weavie.Core.Sessions;

/// <summary>Arguments for creating a new session (all optional — the host fills sensible defaults).</summary>
public sealed record NewSessionRequest {
	/// <summary>The branch (and worktree) name to create; <c>null</c> ⇒ the host prompts or auto-names.</summary>
	public string? Branch { get; init; }

	/// <summary>The base to branch from: <c>"current"</c> (the active session's HEAD), <c>"main"</c>, or a ref; <c>null</c> ⇒ host default. Ignored when <see cref="AttachExisting"/> is set.</summary>
	public string? Base { get; init; }

	/// <summary>An optional first prompt to seed into the new session's agent.</summary>
	public string? Prompt { get; init; }

	/// <summary>The provider for this new session; <c>null</c> means the host's default provider setting.</summary>
	public string? AgentProviderId { get; init; }

	/// <summary>
	/// When true, <see cref="Branch"/> names an <em>existing</em> branch to check out into a new worktree
	/// (no new branch, no base); if a session already exists for it, the host switches to that instead.
	/// </summary>
	public bool AttachExisting { get; init; }
}

/// <summary>Arguments for forking the current session into a new worktree off its HEAD.</summary>
public sealed record ForkSessionRequest {
	/// <summary>The new branch (and worktree) name; <c>null</c> ⇒ the host derives one.</summary>
	public string? Branch { get; init; }

	/// <summary>The handoff brief seeded as the fork's first prompt (the forking Claude's own summary).</summary>
	public string? Handoff { get; init; }
}

/// <summary>
/// The host-side operations behind Weavie's session commands — create/fork/close spawn or tear down a
/// session's native window backend, so they're implemented per host and invoked through this seam.
/// </summary>
public interface ISessionHost {
	/// <summary>Creates a new session on its own worktree + branch, optionally seeding a first prompt.</summary>
	Task<CommandResult> NewSessionAsync(NewSessionRequest request, CancellationToken ct = default);

	/// <summary>Forks the current session into a new worktree off its HEAD, carrying a handoff brief.</summary>
	Task<CommandResult> ForkSessionAsync(ForkSessionRequest request, CancellationToken ct = default);

	/// <summary>Loads a dormant session's backend (by <paramref name="sessionId"/>) in the background, without switching to it.</summary>
	Task<CommandResult> LoadSessionAsync(string? sessionId, CancellationToken ct = default);

	/// <summary>Unloads a session (the active one, or the given <paramref name="sessionId"/>) into a dormant chip, keeping its worktree on disk.</summary>
	Task<CommandResult> UnloadSessionAsync(string? sessionId, CancellationToken ct = default);

	/// <summary>
	/// Deletes the session named by the required <paramref name="sessionId"/>: removes its git worktree but keeps
	/// the branch. Refuses when the worktree has uncommitted changes unless <paramref name="force"/>. A blank id is
	/// rejected — it must never fall back to the focused session, which may not be the caller's own (issue #217).
	/// </summary>
	Task<CommandResult> DeleteSessionAsync(string? sessionId, bool force, CancellationToken ct = default);

	/// <summary>
	/// Classifies a session's worktree for the delete confirm without deleting anything: the result's
	/// <see cref="CommandResult.DataJson"/> carries <c>{ state, label }</c> where <c>state</c> is
	/// <c>clean</c>/<c>untracked</c>/<c>modified</c>, so the UI can escalate the confirmation. The interactive
	/// delete classifies first, then deletes with <c>force</c> on confirm.
	/// </summary>
	Task<CommandResult> ClassifyDeleteAsync(string? sessionId, CancellationToken ct = default);
}
