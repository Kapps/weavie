import { For, type JSX, createSignal, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";
import { connectedBackends } from "../bridge";

// Prompt for a new worktree session: pick the LOCATION (default/local, or a registered remote agent), name
// the branch, then choose what to branch from. Keyboard-first — Enter branches off the active session's HEAD,
// Shift+Enter off main, Esc cancels. Selecting a remote location creates the worktree on that remote box and
// points its session's Claude/terminal/editor at the remote filesystem. "Add remote…" opens registration.
export function NewSessionPrompt(props: {
  onCreate: (branch: string, base: "head" | "main", backendId: string) => void;
  onCancel: () => void;
  onAddRemote: () => void;
}): JSX.Element {
  let input!: HTMLInputElement;
  const [backendId, setBackendId] = createSignal("local");

  const submit = (base: "head" | "main"): void => {
    const branch = input.value.trim();
    if (branch.length > 0) {
      props.onCreate(branch, base, backendId());
    }
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Enter") {
      event.preventDefault();
      event.stopPropagation();
      submit(event.shiftKey ? "main" : "head");
    } else if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      props.onCancel();
    }
  };
  onMount(() => window.addEventListener("keydown", onKeyDown, { capture: true }));
  onCleanup(() => window.removeEventListener("keydown", onKeyDown, { capture: true }));

  return (
    <Portal>
      <div class="modal-backdrop" onPointerDown={() => props.onCancel()}>
        <div
          class="confirm-dialog session-prompt"
          onPointerDown={(event) => event.stopPropagation()}
        >
          <div class="confirm-title">New session</div>
          <div class="confirm-body">
            A session runs on its own git worktree + branch. Pick where it runs, name the branch,
            then choose what to branch from.
          </div>
          {/* Location: the local/default host or a registered remote agent. A remote spins the worktree up
              on that box and points Claude/terminal/editor at its filesystem. */}
          <label class="session-prompt-location">
            <span class="session-prompt-location-label">Location</span>
            <select
              class="session-prompt-select"
              onChange={(event) => {
                if (event.currentTarget.value === "__add__") {
                  event.currentTarget.value = backendId();
                  props.onAddRemote();
                } else {
                  setBackendId(event.currentTarget.value);
                }
              }}
            >
              <For each={connectedBackends()}>
                {(backend) => (
                  <option value={backend.id} selected={backend.id === backendId()}>
                    {backend.isLocal ? "default (local)" : backend.name}
                  </option>
                )}
              </For>
              <option value="__add__">Add remote agent…</option>
            </select>
          </label>
          <input
            class="session-prompt-input"
            type="text"
            placeholder="branch name"
            spellcheck={false}
            autocomplete="off"
            ref={(el) => {
              input = el;
              queueMicrotask(() => el.focus());
            }}
          />
          <div class="session-prompt-actions">
            <button
              type="button"
              class="session-prompt-btn"
              onClick={() => props.onCancel()}
              title="Cancel (Esc)"
            >
              <span class="session-prompt-btn-label">Cancel</span>
              <span class="session-prompt-btn-key">Esc</span>
            </button>
            <button
              type="button"
              class="session-prompt-btn"
              onClick={() => submit("main")}
              title="Branch off main (Shift+Enter)"
            >
              <span class="session-prompt-btn-label">Off main</span>
              <span class="session-prompt-btn-key">Shift+Enter</span>
            </button>
            <button
              type="button"
              class="session-prompt-btn session-prompt-btn-primary"
              onClick={() => submit("head")}
              title="Branch off HEAD (Enter)"
            >
              <span class="session-prompt-btn-label">Off HEAD</span>
              <span class="session-prompt-btn-key">Enter</span>
            </button>
          </div>
        </div>
      </div>
    </Portal>
  );
}
