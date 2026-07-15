import { For, type JSX } from "solid-js";
import type { Suggestion, SuggestionAction } from "../bridge";
import { formatKey } from "../commands/keybindings";
import { findCommand, runCommandWithFeedback } from "../commands/registry";

// A RunCommand button's label with its command's effective shortcut appended ("Yes (Ctrl+…)"); an unbound or
// non-command action shows just the label.
function actionLabel(action: SuggestionAction): string {
  if (action.kind !== "RunCommand" || action.commandId === undefined) {
    return action.label;
  }
  const keys = findCommand(action.commandId)?.keys ?? [];
  return keys.length > 0 ? `${action.label} (${keys.map(formatKey).join(" / ")})` : action.label;
}

// A bottom-right stack of dismissible suggestion cards: contextual nudges (e.g. configure a worktree setup
// command). Distinct from the top-center transient toasts — these persist until acted on or dismissed.
export function Suggestions(props: {
  items: Suggestion[];
  onDismiss: (id: string, forever: boolean) => void;
}): JSX.Element {
  const onAction = (suggestion: Suggestion, action: SuggestionAction): void => {
    if (action.kind === "RunCommand" && action.commandId !== undefined) {
      const args = action.argsJson === undefined ? undefined : JSON.parse(action.argsJson);
      void runCommandWithFeedback(action.commandId, args);
      // Taking the offer is engagement: snooze the card (a fresh run re-offers if nothing came of it).
      props.onDismiss(suggestion.id, false);
    } else {
      props.onDismiss(suggestion.id, action.kind === "DismissForever");
    }
  };
  return (
    <div class="suggestions">
      <For each={props.items}>
        {(suggestion) => (
          <div class="suggestion" role="note">
            <div class="suggestion-title">{suggestion.title}</div>
            <div class="suggestion-body">{suggestion.body}</div>
            <div class="suggestion-actions">
              <For each={suggestion.actions}>
                {(action) => (
                  <button
                    type="button"
                    class="suggestion-action"
                    classList={{ primary: action.kind === "RunCommand" }}
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
