using System.Text.Json;
using Weavie.Core.Git;

namespace Weavie.Hosting;

// Pushes the active session's git branch + dirty flag to the page's terminal-column footer. Added to HostCore
// (not per-OS) so all four hosts surface it — see docs/specs/host-core-unification.md.
public sealed partial class HostCore {
	/// <summary>
	/// Reads the active session's worktree git branch + dirty flag off the hot path and pushes a
	/// <c>git-status</c> to the page. A non-repo / git read failure pushes a null branch, so the footer simply
	/// hides its branch segment. The result is dropped if the active session changed mid-read (switch-race).
	/// </summary>
	private void PushGitStatus() {
		if (_session is not { } session) {
			return;
		}

		string root = session.WorkspaceRoot;
		_ = Task.Run(async () => {
			string? branch = null;
			bool dirty = false;
			try {
				var git = new GitService();
				branch = await git.GetCurrentBranchAsync(root).ConfigureAwait(false);
				dirty = await git.HasUncommittedChangesAsync(root).ConfigureAwait(false);
			} catch (GitException) {
				// Not a git repo, or git unavailable — the footer shows no branch (the honest "unknown" state).
			}

			// Guard + post on the UI thread: a switch that landed while we were reading means this is another
			// worktree's branch — drop it, and never let it check active, get preempted, and still paint late.
			_ui.Post(() => {
				if (ReferenceEquals(_session, session)) {
					_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "git-status", branch, dirty }));
				}
			});
		});
	}
}
