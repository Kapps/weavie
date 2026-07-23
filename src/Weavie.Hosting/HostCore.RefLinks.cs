using System.Text.Json;

namespace Weavie.Hosting;

// Pushes the workspace's forge ref-link prefix — what a terminal "#N" links to — so #123 in any terminal
// (notably the Claude pane) becomes a link to its issue/PR page. Resolved from the workspace's origin remote off
// the hot path. The origin is shared by every worktree session, so it's pushed once on ready (and, since `ready`
// re-fires, on every reconnect) — not per session-switch or per-turn, unlike the branch that git-status tracks.
public sealed partial class HostCore {
	private long _readyReplayGeneration;

	/// <summary>
	/// Resolves the workspace's <c>origin</c> to its forge ref-link prefix (<c>https://host/owner/repo/pull/</c>)
	/// off the hot path and pushes a <c>ref-link-base</c> to the page. A non-forge origin pushes <c>null</c>, so a
	/// terminal <c>#N</c> stays plain text. The result is dropped if the session or ready replay changed mid-read.
	/// </summary>
	private void PushRefLinkBase(long readyGeneration) {
		if (_session is not { } session) {
			return;
		}

		_ = Task.Run(async () => {
			var repo = await ResolveOriginRepoAsync(CancellationToken.None).ConfigureAwait(false);
			string? prefix = repo is null ? null : _pullRequests.RefUrlBase(repo);
			_ui.Post(() => {
				if (Volatile.Read(ref _readyReplayGeneration) == readyGeneration
					&& ReferenceEquals(_session, session)) {
					_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "ref-link-base", prefix }));
				}
			});
		});
	}
}
