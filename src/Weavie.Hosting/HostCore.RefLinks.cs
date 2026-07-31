using System.Text.Json;

namespace Weavie.Hosting;

// Pushes the workspace's forge ref-link prefix — what a terminal "#N" links to — so #123 in any terminal
// (notably the Claude pane) becomes a link to its issue/PR page. Resolved from the workspace's origin remote off
// the hot path. The origin is shared by every worktree, but each session receives the result on its own bus.
public sealed partial class HostCore {
	/// <summary>
	/// Resolves the workspace's <c>origin</c> to its forge ref-link prefix (<c>https://host/owner/repo/pull/</c>)
	/// off the hot path and pushes a <c>ref-link-base</c> to the page. A non-forge origin pushes <c>null</c>, so a
	/// terminal <c>#N</c> stays plain text.
	/// </summary>
	private void PushRefLinkBase(HostSession session) =>
		PushRefLinkBase(session, session.Bus.BroadcastTarget);

	private void PushRefLinkBase(HostSession session, Messaging.MessageTarget target) {
		_ = session.Background.Run(async ct => {
			var repo = await ResolveOriginRepoAsync(ct).ConfigureAwait(false);
			string? prefix = repo is null ? null : _pullRequests.RefUrlBase(repo);
			ct.ThrowIfCancellationRequested();
			target.Feature("git").Publish("refLinkBase", new { prefix });
		});
	}
}
