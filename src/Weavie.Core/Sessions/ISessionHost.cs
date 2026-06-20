using Weavie.Core.Commands;

namespace Weavie.Core.Sessions;

/// <summary>Arguments for creating a new session (all optional — the host fills sensible defaults).</summary>
public sealed record NewSessionRequest {
	/// <summary>The branch (and worktree) name to create; <c>null</c> ⇒ the host prompts or auto-names.</summary>
	public string? Branch { get; init; }

	/// <summary>The base to branch from: <c>"current"</c> (the active session's HEAD), <c>"main"</c>, or a ref; <c>null</c> ⇒ host default.</summary>
	public string? Base { get; init; }

	/// <summary>An optional first prompt to seed into the new session's Claude.</summary>
	public string? Prompt { get; init; }
}

/// <summary>Arguments for forking the current session into a new worktree off its HEAD.</summary>
public sealed record ForkSessionRequest {
	/// <summary>The new branch (and worktree) name; <c>null</c> ⇒ the host derives one.</summary>
	public string? Branch { get; init; }

	/// <summary>The handoff brief seeded as the fork's first prompt (the forking Claude's own summary).</summary>
	public string? Handoff { get; init; }
}

/// <summary>
/// The host-side operations behind Weavie's session commands. The command declarations and argument
/// parsing live in Core (so the palette, MCP, and keybindings see them); the actual create/fork/close —
/// which spawn or tear down a session's native window backend — are implemented per host and invoked
/// through this seam, keeping the logic in Core and the hosts thin.
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
	/// Deletes a session (the active one, or the given <paramref name="sessionId"/>): removes its git worktree
	/// but keeps the branch. Refuses when the worktree has uncommitted changes unless <paramref name="force"/>.
	/// </summary>
	Task<CommandResult> DeleteSessionAsync(string? sessionId, bool force, CancellationToken ct = default);
}
