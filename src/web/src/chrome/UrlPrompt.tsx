import { createSignal, type JSX, Show } from "solid-js";
import { ModalShell, modalSubmitKeys } from "./ModalShell";

// Normalize user input to an http(s) URL: a bare host gets an https:// scheme, and only http/https is accepted.
// Returns the normalized URL, or null when the input isn't a valid http(s) URL.
function normalizeUrl(raw: string): string | null {
  const trimmed = raw.trim();
  if (trimmed.length === 0) {
    return null;
  }
  const withScheme = /^[a-z][a-z0-9+.-]*:\/\//i.test(trimmed) ? trimmed : `https://${trimmed}`;
  try {
    const url = new URL(withScheme);
    return url.protocol === "http:" || url.protocol === "https:" ? url.toString() : null;
  } catch {
    return null;
  }
}

/**
 * Portaled modal prompt for an http(s) URL to open in a web tab (Enter submits, Escape / backdrop cancels; a bare
 * host is assumed https). Reuses ConfirmDialog's shell + capture-phase Enter/Escape.
 */
export function UrlPrompt(props: {
  onSubmit: (url: string) => void;
  onCancel: () => void;
}): JSX.Element {
  const [value, setValue] = createSignal("");
  const [invalid, setInvalid] = createSignal(false);

  const submit = (): void => {
    const url = normalizeUrl(value());
    if (url === null) {
      setInvalid(true);
      return;
    }
    props.onSubmit(url);
  };

  const onKeyDown = modalSubmitKeys(submit, props.onCancel);

  return (
    <ModalShell labelledBy="url-prompt-title" onDismiss={props.onCancel} onKeyDown={onKeyDown}>
      <div class="confirm-title" id="url-prompt-title">
        Open URL
      </div>
      <div class="confirm-body">
        Open an http(s) page in a web tab — e.g. a local dev server to preview your app.
      </div>
      <input
        ref={(element) => {
          queueMicrotask(() => element.focus());
        }}
        class="url-prompt-input"
        type="text"
        placeholder="localhost:3000"
        value={value()}
        onInput={(event) => {
          setValue(event.currentTarget.value);
          setInvalid(false);
        }}
      />
      <Show when={invalid()}>
        <div class="url-prompt-error">Enter a valid http(s) URL.</div>
      </Show>
      <div class="confirm-actions">
        <button type="button" class="confirm-btn" onClick={() => props.onCancel()}>
          Cancel
        </button>
        <button type="button" class="confirm-btn confirm-btn-primary" onClick={() => submit()}>
          Open
        </button>
      </div>
    </ModalShell>
  );
}
