using System.Text;
using System.Text.Json;
using Weavie.Core;
using Weavie.Core.FileSystem;
using Weavie.Core.Suggestions;

namespace Weavie.Hosting;

// Contextual suggestions: the per-workspace SuggestionService evaluates the registered nudges, this partial
// constructs it, fans the active set out to the page, applies dismissals, and seeds the worktree-setup prompt.
// See docs/specs/suggestions.md.
public sealed partial class HostCore {
	private SuggestionService? _suggestions;

	// The analysis prompt for the worktree-setup nudge. Pre-filled (not auto-sent) into the primary session so
	// Claude proposes a command and asks the user to confirm; on confirmation it persists via the setSetting tool.
	private const string SetupCommandPrompt =
		"Look at this repository and decide a single shell command suitable for the `worktree.setupCommand` " +
		"setting — the one command needed to make a fresh checkout ready to work in (install dependencies, and a " +
		"build step only if required before editing). Briefly explain your choice and ask me to confirm. On my " +
		"confirmation, call the `setSetting` tool with key `worktree.setupCommand`. Don't run anything else.";

	private void InitSuggestions() {
		var dismissals = new SuggestionDismissals(new LocalFileSystem(), WeaviePaths.WorkspaceSuggestionsFile(Id));
		dismissals.Log += Log;
		_suggestions = new SuggestionService(
			_suggestionRegistry, _settings, new LocalFileSystem(), WorkspaceRoot, dismissals, PushSuggestions);
	}

	// Fan the active suggestion set out to every client, like the session list.
	private void PushSuggestions(IReadOnlyList<SuggestionDefinition> active) =>
		_bridge.PostToWeb(JsonSerializer.Serialize(new {
			type = "suggestions",
			items = active.Select(s => new {
				id = s.Id,
				title = s.Title,
				body = s.Body,
				actions = s.Actions.Select(a => new {
					label = a.Label,
					kind = a.Kind.ToString(),
					commandId = a.CommandId,
					argsJson = a.ArgsJson,
				}),
			}),
		}));

	// Apply a web dismissal ("not now" → snooze for this run; "don't ask again" → forever); the service re-pushes.
	private void DismissSuggestion(string id, bool forever) {
		if (string.IsNullOrEmpty(id) || _suggestions is null) {
			return;
		}

		if (forever) {
			_suggestions.DismissForever(id);
		} else {
			_suggestions.Snooze(id);
		}
	}

	// "Yes" on the worktree-setup card: pre-fill (no Enter) the analysis prompt into the primary session's Claude,
	// so it proposes a setupCommand and asks the user to confirm. Never auto-sends — the user presses Enter.
	private void SeedSetupCommandPrompt() {
		if (_primarySession is { } primary) {
			primary.Claude.Write(Encoding.UTF8.GetBytes(SetupCommandPrompt));
		}
	}
}
