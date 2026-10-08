using System.Text.Json;
using Weavie.Core.Git;
using Weavie.Core.Json;
using Weavie.Core.Workspaces;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// Find-in-files over one owning session's worktree.
public sealed partial class HostCore {
	private async Task<JsonElement> SearchInFilesAsync(
		HostSession session,
		JsonElement root,
		CancellationToken ct) {
		string query = root.GetStringOrEmpty("query");
		var options = new GrepOptions {
			CaseSensitive = root.GetBoolOrFalse("caseSensitive"),
			WholeWord = root.GetBoolOrFalse("wholeWord"),
			Regex = root.GetBoolOrFalse("regex"),
			Include = root.GetStringOrEmpty("include"),
			Exclude = root.GetStringOrEmpty("exclude"),
			ExcludeGitignored = root.GetBoolOr("excludeGitignored", fallback: true),
		};
		string workspaceRoot = session.WorkspaceRoot;
		var matches = new List<SearchMatchWire>();
		bool truncated = false;
		string? error = null;
		if (query.Length > 0) {
			try {
				var result = await new GitService()
					.GrepAsync(workspaceRoot, query, options, ct)
					.ConfigureAwait(false);
				truncated = result.Truncated;
				foreach (var m in result.Matches) {
					matches.Add(new SearchMatchWire(
						WorkspacePaths.CanonicalFsPath(Path.GetFullPath(Path.Combine(workspaceRoot, m.Path))),
						m.Line,
						m.Column,
						m.Preview));
				}
			} catch (GitException ex) {
				error = ex.Message;
				Log($"[weavie] find-in-files failed: {ex.Message}");
			}
		}

		return JsonSerializer.SerializeToElement(new SearchResultWire(query, matches, truncated, error), WireJson.Default.SearchResultWire);
	}
}

internal sealed record SearchResultWire(string Query, IReadOnlyList<SearchMatchWire> Matches, bool Truncated, string? Error);

internal sealed record SearchMatchWire(string Path, int Line, int Column, string Preview);
