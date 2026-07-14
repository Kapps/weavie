using System.Text.Json;
using Weavie.Core.Git;
using Weavie.Core.Json;
using Weavie.Core.Workspaces;

namespace Weavie.Hosting;

// Find-in-files: the content search over the active session's worktree (git grep), shared by every host.
public sealed partial class HostCore {
	/// <summary>
	/// Runs the find-in-files content search over the active session's worktree (<c>git grep</c>) and pushes the
	/// matches (each with a canonical absolute path so a click reuses an open tab, and the first match's UTF-16
	/// column so the editor lands on it). The request <c>token</c> is echoed back so the page can drop a stale
	/// reply for a search the user has since changed; an empty query clears results without running git. A git
	/// failure (e.g. a bad regex) sets <c>error</c> so the panel says the search failed, never "No results".
	/// </summary>
	private async Task SearchInFilesAsync(JsonElement root) {
		if (_session is not { } session) {
			return;
		}

		int token = root.GetIntOr("token", 0);
		string query = root.GetStringOrEmpty("query");
		var options = new GrepOptions {
			CaseSensitive = root.GetBoolOrFalse("caseSensitive"),
			WholeWord = root.GetBoolOrFalse("wholeWord"),
			Regex = root.GetBoolOrFalse("regex"),
			Include = root.GetStringOrEmpty("include"),
			Exclude = root.GetStringOrEmpty("exclude"),
		};
		string workspaceRoot = session.WorkspaceRoot;
		var matches = new List<object>();
		bool truncated = false;
		string? error = null;
		if (query.Length > 0) {
			try {
				var result = await new GitService().GrepAsync(workspaceRoot, query, options, CancellationToken.None).ConfigureAwait(false);
				truncated = result.Truncated;
				foreach (var m in result.Matches) {
					matches.Add(new {
						path = WorkspacePaths.CanonicalFsPath(Path.GetFullPath(Path.Combine(workspaceRoot, m.Path))),
						line = m.Line,
						column = m.Column,
						preview = m.Preview,
					});
				}
			} catch (GitException ex) {
				error = ex.Message;
				Log($"[weavie] find-in-files failed: {ex.Message}");
			}
		}

		// Guard + post on the UI thread, so a slow grep can't check active, lose to a switch, and still post.
		_ui.Post(() => {
			if (ReferenceEquals(_session, session)) {
				_bridge.PostToWeb(JsonSerializer.Serialize(new { type = "find-in-files-results", token, query, matches, truncated, error }));
			}
		});
	}
}
