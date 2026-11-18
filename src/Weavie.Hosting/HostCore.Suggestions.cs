using Weavie.Core;
using Weavie.Core.Mcp;
using Weavie.Core.Suggestions;
using Weavie.Core.Workspaces;
using Weavie.Hosting.Messaging;

namespace Weavie.Hosting;

// Contextual suggestions: each repo's SuggestionService evaluates the registered nudges; this partial pushes its
// active set to the page under the repo's root, applies dismissals, and seeds the workspace-setup prompt.
// See docs/specs/suggestions.md.
public sealed partial class HostCore {
	private void EvaluateSuggestions() {
		foreach (var repo in _repos.Values) {
			repo.Suggestions.Evaluate();
		}
	}

	private void PushSuggestions(string repo, IReadOnlyList<ActiveSuggestion> active) =>
		_messages.Host.Feature("suggestions").Publish(
			"changed", WireJson.Default.SuggestionsChanged, new(repo, SuggestionWires(active)));

	private static SuggestionWire[] SuggestionWires(IReadOnlyList<ActiveSuggestion> active) => [
		.. active.Select(s => new SuggestionWire(
			s.Definition.Id,
			s.Definition.Title,
			s.Body,
			[.. s.Definition.Actions.Select(a => new SuggestionActionWire(a.Label, a.Kind.ToString(), a.CommandId, a.ArgsJson))])),
	];

	// Apply a web dismissal ("not now" → snooze for this run; "don't ask again" → forever); the service re-pushes.
	private static void DismissSuggestion(HostSession session, string id, bool forever) {
		if (string.IsNullOrEmpty(id)) {
			return;
		}

		if (forever) {
			session.Repo.Suggestions.DismissForever(id);
		} else {
			session.Repo.Suggestions.Snooze(id);
		}
	}

	// "Yes" on the workspace-setup card: pre-fill (no Enter) the setup prompt into the owning session's agent,
	// so it proposes the settings and asks the user to confirm. Bracketed paste so the multi-line prompt lands as
	// one paste (not line-by-line submits); never auto-sends — the user reviews and presses Enter. The prompt is
	// the same artifact the /mcp__weavie__setup-workspace slash command serves.
	private static void SeedWorkspaceSetup(HostSession session) =>
		session.PrefillAgentPrompt(WorkspaceSetupPrompt.Prompt.Text);
}

internal sealed record SuggestionsChanged(string Repo, IReadOnlyList<SuggestionWire> Items);

internal sealed record SuggestionWire(string Id, string Title, string Body, IReadOnlyList<SuggestionActionWire> Actions);

internal sealed record SuggestionActionWire(string Label, string Kind, string? CommandId, string? ArgsJson);
