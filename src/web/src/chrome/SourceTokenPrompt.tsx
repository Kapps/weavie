import { createSignal, type JSX, Show } from "solid-js";
import type { ClientSession } from "../bridge";
import { submitSourceToken } from "../editor/source/source-store";
import { ModalShell, modalSubmitKeys, PromptActions, PromptButton } from "./ModalShell";

// Paste-your-token dialog for connecting a source (e.g. Notion). The host has already opened the source's token
// page in the browser; the user pastes the personal access token here and we hand it to the host to validate +
// save. On success the dialog closes (the host toasts the workspace); a rejected token is shown inline so the
// user can fix it without restarting. Enter submits, Esc / backdrop cancels. See docs/specs/notion-source-auth.md.
export function SourceTokenPrompt(props: {
  session: ClientSession;
  sourceId: string;
  label: string;
  onClose: () => void;
}): JSX.Element {
  const [token, setToken] = createSignal("");
  const [submitting, setSubmitting] = createSignal(false);
  const [error, setError] = createSignal<string | null>(null);

  const submit = async (): Promise<void> => {
    const value = token().trim();
    if (value === "" || submitting()) {
      return;
    }
    setSubmitting(true);
    setError(null);
    const result = await submitSourceToken(props.session, props.sourceId, value);
    if (result.ok) {
      props.onClose();
    } else {
      setError(result.error || "That token wasn't accepted. Check it and try again.");
      setSubmitting(false);
    }
  };

  const onKeyDown = modalSubmitKeys(() => void submit(), props.onClose);
  return (
    <ModalShell
      labelledBy="source-token-title"
      onDismiss={props.onClose}
      onKeyDown={onKeyDown}
      class="session-prompt"
    >
      <div class="confirm-title" id="source-token-title">
        Connect {props.label}
      </div>
      <div class="confirm-body">
        We opened {props.label}'s token page in your browser. Create a personal access token there,
        then paste it here to connect.
      </div>
      <div class="session-prompt-field">
        <input
          class="session-prompt-input"
          type="password"
          placeholder={`Paste your ${props.label} token`}
          spellcheck={false}
          autocomplete="off"
          disabled={submitting()}
          value={token()}
          onInput={(event) => {
            setToken(event.currentTarget.value);
            setError(null);
          }}
          ref={(el) => {
            queueMicrotask(() => el.focus());
          }}
        />
        <Show when={error()}>
          {(message) => (
            <div class="session-prompt-error" role="alert">
              {message()}
            </div>
          )}
        </Show>
      </div>
      <PromptActions>
        <PromptButton label="Cancel" shortcut="Esc" title="Cancel (Esc)" onClick={props.onClose} />
        <PromptButton
          label={submitting() ? "Connecting…" : "Connect"}
          shortcut="Enter"
          title="Connect (Enter)"
          onClick={() => void submit()}
          disabled={token().trim() === "" || submitting()}
          primary
        />
      </PromptActions>
    </ModalShell>
  );
}
