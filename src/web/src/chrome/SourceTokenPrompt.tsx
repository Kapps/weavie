import { type JSX, Show, createEffect, createSignal, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";
import { activeBackendId, submitSourceToken } from "../bridge";

// Paste-your-token dialog for connecting a source (e.g. Notion). The host has already opened the source's token
// page in the browser; the user pastes the personal access token here and we hand it to the host to validate +
// save. On success the dialog closes (the host toasts the workspace); a rejected token is shown inline so the
// user can fix it without restarting. Enter submits, Esc / backdrop cancels. See docs/specs/notion-source-auth.md.
export function SourceTokenPrompt(props: {
  sourceId: string;
  label: string;
  onClose: () => void;
}): JSX.Element {
  const [token, setToken] = createSignal("");
  const [submitting, setSubmitting] = createSignal(false);
  const [error, setError] = createSignal<string | null>(null);

  // A backend switch abandons the connect flow — the validate reply routes to the backend that was active at
  // submit, so on a switch the dialog would hang on "Connecting…". Close it instead (the user can reconnect).
  const backendAtOpen = activeBackendId();
  createEffect(() => {
    if (activeBackendId() !== backendAtOpen) {
      props.onClose();
    }
  });

  const submit = async (): Promise<void> => {
    const value = token().trim();
    if (value === "" || submitting()) {
      return;
    }
    setSubmitting(true);
    setError(null);
    const result = await submitSourceToken(props.sourceId, value);
    if (result.ok) {
      props.onClose();
    } else {
      setError(result.error || "That token wasn't accepted. Check it and try again.");
      setSubmitting(false);
    }
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Enter") {
      event.preventDefault();
      event.stopPropagation();
      void submit();
    } else if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      props.onClose();
    }
  };
  onMount(() => window.addEventListener("keydown", onKeyDown, { capture: true }));
  onCleanup(() => window.removeEventListener("keydown", onKeyDown, { capture: true }));

  return (
    <Portal>
      <div class="modal-backdrop" onPointerDown={() => props.onClose()}>
        <div
          class="confirm-dialog session-prompt"
          role="dialog"
          aria-modal="true"
          aria-labelledby="source-token-title"
          onPointerDown={(event) => event.stopPropagation()}
        >
          <div class="confirm-title" id="source-token-title">
            Connect {props.label}
          </div>
          <div class="confirm-body">
            We opened {props.label}'s token page in your browser. Create a personal access token
            there, then paste it here to connect.
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
          <div class="session-prompt-actions">
            <button
              type="button"
              class="session-prompt-btn"
              onClick={() => props.onClose()}
              title="Cancel (Esc)"
            >
              <span class="session-prompt-btn-label">Cancel</span>
              <span class="session-prompt-btn-key">Esc</span>
            </button>
            <button
              type="button"
              class="session-prompt-btn session-prompt-btn-primary"
              disabled={token().trim() === "" || submitting()}
              onClick={() => void submit()}
              title="Connect (Enter)"
            >
              <span class="session-prompt-btn-label">
                {submitting() ? "Connecting…" : "Connect"}
              </span>
              <span class="session-prompt-btn-key">Enter</span>
            </button>
          </div>
        </div>
      </div>
    </Portal>
  );
}
