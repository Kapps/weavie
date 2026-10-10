import { createSignal, type JSX, onMount, Show } from "solid-js";
import { controlMenuOpen } from "../chrome/ControlMenu";
import { ModalShell } from "../chrome/ModalShell";
import { keyLabel } from "../commands/key-hint";
import { CommandIds } from "../commands/types";
import {
  inferenceControls,
  openInferenceControls,
  setSuggestionsDialogOpen,
  suggestionsDialogOpen,
} from "./inference-controls";
import { type Attempt, SuggestionSettings } from "./SuggestionSettings";

/** Configure Suggestions: the setup step's controls, any time after setup. */
export function SuggestionsDialog(): JSX.Element {
  return (
    <Show when={suggestionsDialogOpen()}>
      <SuggestionsDialogBody />
    </Show>
  );
}

function SuggestionsDialogBody(): JSX.Element {
  const [error, setError] = createSignal<string | null>(null);
  const attempt: Attempt = (action) => {
    setError(null);
    action().catch((caught: unknown) =>
      setError(caught instanceof Error ? caught.message : String(caught)),
    );
  };
  const close = () => setSuggestionsDialogOpen(false);
  onMount(() => attempt(openInferenceControls));
  return (
    <ModalShell
      labelledBy="suggestions-dialog-title"
      class="suggestions-dialog"
      onDismiss={close}
      onKeyDown={(event) => {
        // An open picker owns Escape: it closes the picker, not the dialog.
        if (event.key === "Escape" && !controlMenuOpen()) {
          event.preventDefault();
          close();
        }
      }}
    >
      {/* Takes focus from the editor behind, so Tab starts at the dialog's first control. */}
      <span tabindex="-1" ref={(element) => queueMicrotask(() => element.focus())} />
      <header class="sg-dialog-header">
        <h2 id="suggestions-dialog-title">Suggestions</h2>
        <span>{keyLabel(CommandIds.configureSuggestions)}</span>
      </header>
      <p class="sg-dialog-hint">Small helpers from your agent, like a name for a new branch.</p>
      <Show
        when={inferenceControls()}
        fallback={<p class="sg-dialog-hint">Loading your suggestion settings…</p>}
      >
        {(state) => <SuggestionSettings state={state()} attempt={attempt} />}
      </Show>
      <Show when={error()}>
        {(message) => (
          <p class="sg-dialog-error" role="alert">
            {message()}
          </p>
        )}
      </Show>
      <footer class="sg-dialog-footer">
        <small>Tab Move · Enter or ↓ Open · Esc Close</small>
        <button type="button" class="sg-done" onClick={close}>
          Done
        </button>
      </footer>
    </ModalShell>
  );
}
