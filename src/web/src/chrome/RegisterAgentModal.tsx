import { type JSX, createSignal, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";
import { log } from "../bridge";
import { type RemoteAgent, addAgent } from "./remote-agents";

// Register a remote agent: a friendly name + the runner's control-plane URL and token (printed in the
// runner's console at startup, reachable over Tailscale). On save we persist it and connect, so it appears as
// a New Session location. Esc cancels; Enter saves.
export function RegisterAgentModal(props: {
  onClose: () => void;
  onAdded: (name: string) => void;
}): JSX.Element {
  const [name, setName] = createSignal("");
  const [url, setUrl] = createSignal("");
  const [token, setToken] = createSignal("");
  const [busy, setBusy] = createSignal(false);
  const [error, setError] = createSignal<string | null>(null);

  const canSave = (): boolean =>
    name().trim() !== "" && url().trim() !== "" && token().trim() !== "" && !busy();

  const save = async (): Promise<void> => {
    if (!canSave()) {
      return;
    }
    setBusy(true);
    setError(null);
    const agent: RemoteAgent = { name: name().trim(), url: url().trim(), token: token().trim() };
    try {
      await addAgent(agent);
      props.onAdded(agent.name);
      props.onClose();
    } catch (err) {
      const message = String(err);
      setError(message);
      log("error", `register remote agent failed: ${message}`);
    } finally {
      setBusy(false);
    }
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      props.onClose();
    } else if (event.key === "Enter") {
      event.preventDefault();
      event.stopPropagation();
      void save();
    }
  };
  onMount(() => window.addEventListener("keydown", onKeyDown, { capture: true }));
  onCleanup(() => window.removeEventListener("keydown", onKeyDown, { capture: true }));

  return (
    <Portal>
      <div class="modal-backdrop" onPointerDown={() => props.onClose()}>
        <div
          class="confirm-dialog session-prompt"
          onPointerDown={(event) => event.stopPropagation()}
        >
          <div class="confirm-title">Add remote agent</div>
          <div class="confirm-body">
            Point Weavie at a remote runner. URL + token are printed in the runner's console at
            startup (reachable over Tailscale, e.g. http://your-host:8800).
          </div>
          <input
            class="session-prompt-input"
            type="text"
            placeholder="name (e.g. devbox)"
            spellcheck={false}
            autocomplete="off"
            value={name()}
            onInput={(e) => setName(e.currentTarget.value)}
            ref={(el) => queueMicrotask(() => el.focus())}
          />
          <input
            class="session-prompt-input"
            type="text"
            placeholder="runner URL (http://host:8800)"
            spellcheck={false}
            autocomplete="off"
            value={url()}
            onInput={(e) => setUrl(e.currentTarget.value)}
          />
          <input
            class="session-prompt-input"
            type="text"
            placeholder="runner token"
            spellcheck={false}
            autocomplete="off"
            value={token()}
            onInput={(e) => setToken(e.currentTarget.value)}
          />
          {error() !== null ? <div class="session-prompt-error">{error()}</div> : null}
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
              disabled={!canSave()}
              onClick={() => void save()}
              title="Save + connect (Enter)"
            >
              <span class="session-prompt-btn-label">{busy() ? "Connecting…" : "Add"}</span>
              <span class="session-prompt-btn-key">Enter</span>
            </button>
          </div>
        </div>
      </div>
    </Portal>
  );
}
