namespace Weavie.Core.Lsp;

/// <summary>
/// Spawns a resolved language server as a child process. The OS/process seam behind the LSP controller — the
/// real implementation starts a <see cref="System.Diagnostics.Process"/>; tests supply a scripted in-memory
/// server so a full LSP round-trip stays deterministic (mirrors <c>IPtyLauncher</c> for terminals).
/// </summary>
public interface ILspServerLauncher {
	/// <summary>
	/// Starts <paramref name="command"/> rooted at <paramref name="workspaceRoot"/>, returning the running server
	/// (un-started: the caller wires <see cref="ILspServerProcess.FrameReceived"/>/<see cref="ILspServerProcess.Exited"/>
	/// then calls <see cref="ILspServerProcess.Start"/>). <paramref name="log"/> receives the server's stderr.
	/// </summary>
	/// <param name="command">The resolved executable + arguments to launch.</param>
	/// <param name="workspaceRoot">The working directory to spawn the server in (the session's worktree).</param>
	/// <param name="log">Diagnostic sink for the server's stderr and lifecycle.</param>
	ILspServerProcess Start(ResolvedCommand command, string workspaceRoot, Action<string> log);
}
