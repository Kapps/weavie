import { createSignal, type JSX, Show } from "solid-js";
import { openUrlExternal } from "../terminal/terminal-links";
import { type CredentialPrompt as Prompt, submitCredential } from "./host-asks";
import { ModalShell, modalSubmitKeys, PromptActions, PromptButton } from "./ModalShell";

// Paste-your-key dialog for any declared service. The host validates the key before saving it; a rejected key is
// shown inline so the user can fix it. Enter submits, Esc / backdrop cancels.
export function CredentialPrompt(props: { prompt: Prompt; onClose: () => void }): JSX.Element {
  const [key, setKey] = createSignal("");
  const [submitting, setSubmitting] = createSignal(false);
  const [error, setError] = createSignal<string | null>(null);

  const submit = async (): Promise<void> => {
    const value = key().trim();
    if (value === "" || submitting()) {
      return;
    }
    setSubmitting(true);
    setError(null);
    const result = await submitCredential(props.prompt.session, props.prompt.id, value);
    if (result.ok) {
      props.onClose();
    } else {
      setError(result.error || "That key wasn't accepted. Check it and try again.");
      setSubmitting(false);
    }
  };

  const onKeyDown = modalSubmitKeys(() => void submit(), props.onClose);
  return (
    <ModalShell
      labelledBy="credential-prompt-title"
      onDismiss={props.onClose}
      onKeyDown={onKeyDown}
      class="session-prompt"
    >
      <div class="confirm-title" id="credential-prompt-title">
        Connect {props.prompt.displayName}
      </div>
      <div class="confirm-body">
        Create your {props.prompt.keyNoun} at{" "}
        <a
          href={props.prompt.keyPageUrl}
          onClick={(event) => {
            event.preventDefault();
            openUrlExternal(props.prompt.keyPageUrl);
          }}
        >
          {props.prompt.keyPageUrl}
        </a>
        , then paste it here.
      </div>
      <div class="session-prompt-field">
        <input
          class="session-prompt-input"
          type="password"
          placeholder={`Paste your ${props.prompt.displayName} ${props.prompt.keyNoun}`}
          spellcheck={false}
          autocomplete="off"
          disabled={submitting()}
          value={key()}
          onInput={(event) => {
            setKey(event.currentTarget.value);
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
          disabled={key().trim() === "" || submitting()}
          primary
        />
      </PromptActions>
    </ModalShell>
  );
}
