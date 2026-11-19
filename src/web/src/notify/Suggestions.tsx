import { For, type JSX } from "solid-js";
import type { ClientSession, Suggestion, SuggestionAction } from "../bridge";
import { keyHint } from "../commands/key-hint";
import { runCommandInSessionWithFeedback } from "../commands/registry";
import { repoStateOf } from "../repo-state";

// A RunCommand button's label with its command's effective shortcut appended ("Yes (Ctrl+…)"); an unbound or
// non-command action shows just the label.
function actionLabel(action: SuggestionAction): string {
  if (action.kind !== "RunCommand" || action.commandId === undefined) {
    return action.label;
  }
  return `${action.label}${keyHint(action.commandId)}`;
}

// A bottom-right stack of dismissible suggestion cards: contextual nudges (e.g. configure a worktree setup
// command). Distinct from the top-center transient toasts — these persist until acted on or dismissed. A card's
// action and its dismissal go to the session the cards were shown for, since the same card exists per repo.
export function Suggestions(props: { session: ClientSession | null }): JSX.Element {
  const onAction = (suggestion: Suggestion, action: SuggestionAction): void => {
    const session = props.session;
    if (session === null) {
      return;
    }
    const dismiss = (forever: boolean): void =>
      session.feature("suggestions").publish("dismiss", { id: suggestion.id, forever });
    if (action.kind === "RunCommand" && action.commandId !== undefined) {
      const args = action.argsJson === undefined ? undefined : JSON.parse(action.argsJson);
      void runCommandInSessionWithFeedback(session, action.commandId, args);
      // Taking the offer is engagement: snooze the card (a fresh run re-offers if nothing came of it).
      dismiss(false);
    } else {
      dismiss(action.kind === "DismissForever");
    }
  };
  return (
    <div class="suggestions">
      <For each={repoStateOf(props.session).suggestions}>
        {(suggestion) => (
          <div class="suggestion" role="note">
            <div class="suggestion-title">{suggestion.title}</div>
            <div class="suggestion-body">{suggestion.body}</div>
            <div class="suggestion-actions">
              <For each={suggestion.actions}>
                {(action) => (
                  <button
                    type="button"
                    class="suggestion-action ui-button"
                    classList={{
                      primary: action.kind === "RunCommand",
                      "ui-button-primary": action.kind === "RunCommand",
                    }}
                    onClick={() => onAction(suggestion, action)}
                  >
                    {actionLabel(action)}
                  </button>
                )}
              </For>
            </div>
          </div>
        )}
      </For>
    </div>
  );
}
