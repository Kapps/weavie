using System.Diagnostics;
using System.Text;

namespace Weavie.Core.Lsp;

/// <summary>
/// The production <see cref="ILspServerLauncher"/>: spawns a resolved language server as a child process with its
/// stdio redirected, rooted at the session's worktree. Returns it un-started so the caller can wire events first.
/// </summary>
public sealed class LspServerLauncher : ILspServerLauncher {
	/// <inheritdoc/>
	public ILspServerProcess Start(ResolvedCommand command, string workspaceRoot, Action<string> log) {
		ArgumentNullException.ThrowIfNull(command);
		ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
		ArgumentNullException.ThrowIfNull(log);

		var psi = new ProcessStartInfo {
			FileName = command.FileName,
			WorkingDirectory = workspaceRoot,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
			StandardErrorEncoding = Encoding.UTF8,
		};
		foreach (string arg in command.Arguments) {
			psi.ArgumentList.Add(arg);
		}

		var process = new Process { StartInfo = psi };
		process.Start();
		return new LspServerProcess(process, command.ServerPath, log);
	}
}
