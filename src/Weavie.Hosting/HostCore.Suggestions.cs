using System.Text.Json;
using Weavie.Core;
using Weavie.Core.FileSystem;
using Weavie.Core.Mcp;
using Weavie.Core.Suggestions;
using Weavie.Core.Workspaces;

namespace Weavie.Hosting;

// Contextual suggestions: the per-workspace SuggestionService evaluates the registered nudges, this partial
// constructs it, fans the active set out to the page, applies dismissals, and seeds the workspace-setup prompt.
// See docs/specs/suggestions.md.
public sealed partial class HostCore {
	private SuggestionService? _suggestions;

	private void InitSuggestions() {
		var fileSystem = new LocalFileSystem();
		var dismissals = new SuggestionDismissals(fileSystem, WeaviePaths.WorkspaceSuggestionsFile(Id));
		dismissals.Log += Log;
		// Built-in auto-config runs as the probe: it detects the language(s), writes the unset setup/test settings,
		// and reports whether a manifest is present (the card gate). Set before the service, whose ctor starts it.
		_autoConfig = new WorkspaceAutoConfig(_settings, WorkspaceRoot);
		_suggestions = new SuggestionService(
			_suggestionRegistry, _settings, fileSystem, WorkspaceRoot, dismissals,
			TimeSpan.FromMilliseconds(500), PushSuggestions, () => RunAutoConfigProbe(fileSystem));
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

	// "Yes" on the workspace-setup card: pre-fill (no Enter) the setup prompt into the primary session's Claude,
	// so it proposes the settings and asks the user to confirm. Bracketed paste so the multi-line prompt lands as
	// one paste (not line-by-line submits); never auto-sends — the user reviews and presses Enter. The prompt is
	// the same artifact the /mcp__weavie__setup-workspace slash command serves.
	private void SeedWorkspaceSetup() {
		if (_primarySession is { } primary) {
			primary.PrefillAgentPrompt(WorkspaceSetupPrompt.Prompt.Text);
		}
	}
}
