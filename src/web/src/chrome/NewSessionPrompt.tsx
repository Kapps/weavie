import { For, type JSX, Show, createEffect, createSignal, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";
import { connectedBackends, requestBranches } from "../bridge";

// Prompt for a new worktree session: pick the location (local or a remote agent), then name the branch via a
// typeahead over the backend's branches — type a new name to create (Enter = off HEAD, Shift+Enter = off
// main), or pick an existing branch to check it out. Esc cancels.
export function NewSessionPrompt(props: {
  // The location to preselect (last-used, or a freshly-added agent); the caller passes a connected backend id.
  initialBackendId: string;
  onCreate: (branch: string, base: "head" | "main", backendId: string) => void;
  onCheckout: (branch: string, backendId: string) => void;
  onCancel: () => void;
  onAddRemote: () => void;
  onDisconnect: (backendId: string) => void;
}): JSX.Element {
  const [backendId, setBackendId] = createSignal(props.initialBackendId);
  const [branch, setBranch] = createSignal("");
  const [branches, setBranches] = createSignal<string[]>([]);
  const [highlight, setHighlight] = createSignal(-1);

  // Load the chosen backend's branches, reloading on location change; ignore a stale reply from a location
  // the user has since switched away from.
  createEffect(() => {
    const id = backendId();
    setBranches([]);
    void requestBranches(id).then((list) => {
      if (backendId() === id) {
        setBranches(list);
      }
    });
  });

  // The display name of the currently-picked location ("" for local), for the Disconnect affordance.
  const selectedRemoteName = (): string =>
    connectedBackends().find((b) => b.id === backendId() && !b.isLocal)?.name ?? "";

  const trimmed = (): string => branch().trim();
  // Existing branches containing the typed text (case-insensitive), minus an exact full match; capped.
  const suggestions = (): string[] => {
    const q = trimmed().toLowerCase();
    if (q.length === 0) {
      return [];
    }
    return branches()
      .filter((b) => b.toLowerCase().includes(q) && b !== trimmed())
      .slice(0, 8);
  };
  const matchedExisting = (): boolean => branches().includes(trimmed());

  const create = (base: "head" | "main"): void => {
    if (trimmed().length > 0) {
      props.onCreate(trimmed(), base, backendId());
    }
  };
  const checkout = (name: string): void => {
    if (name.length > 0) {
      props.onCheckout(name, backendId());
    }
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    const list = suggestions();
    if (event.key === "ArrowDown" && list.length > 0) {
      event.preventDefault();
      event.stopPropagation();
      setHighlight((h) => (h + 1) % list.length);
    } else if (event.key === "ArrowUp" && list.length > 0) {
      event.preventDefault();
      event.stopPropagation();
      setHighlight((h) => (h <= 0 ? list.length - 1 : h - 1));
    } else if (event.key === "Enter") {
      event.preventDefault();
      event.stopPropagation();
      const picked = highlight() >= 0 && highlight() < list.length ? list[highlight()] : undefined;
      if (picked !== undefined) {
        checkout(picked); // arrowed to an existing branch → check it out
      } else if (matchedExisting()) {
        checkout(trimmed()); // typed an existing branch's full name → check it out
      } else {
        create(event.shiftKey ? "main" : "head"); // a new branch name → create it
      }
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
            A session runs on its own git worktree + branch. Pick where it runs, then name a new
            branch or pick an existing one to check out.
          </div>
          {/* Location: the local host or a remote agent. A <div> (not <label>) so the Disconnect button
              doesn't act as a label proxy refocusing the select; Disconnect shows only when a remote is picked. */}
          <div class="session-prompt-location">
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
            <Show when={selectedRemoteName() !== ""}>
              <button
                type="button"
                class="session-prompt-location-remove"
                title={`Disconnect ${selectedRemoteName()} — close its bridge and forget this remote agent`}
                onClick={() => {
                  props.onDisconnect(backendId());
                  setBackendId("local");
                }}
              >
                Disconnect
              </button>
            </Show>
          </div>
          <div class="session-prompt-field">
            <input
              class="session-prompt-input"
              type="text"
              placeholder="branch name"
              spellcheck={false}
              autocomplete="off"
              value={branch()}
              onInput={(event) => {
                setBranch(event.currentTarget.value);
                setHighlight(-1);
              }}
              ref={(el) => {
                queueMicrotask(() => el.focus());
              }}
            />
            <Show when={suggestions().length > 0}>
              <ul class="session-prompt-suggestions">
                <For each={suggestions()}>
                  {(name, i) => (
                    <li
                      class="session-prompt-suggestion"
                      classList={{ active: i() === highlight() }}
                      // pointerdown (not click) so picking a suggestion isn't lost to the input's blur, and
                      // preventDefault keeps focus in the field.
                      onPointerDown={(event) => {
                        event.preventDefault();
                        checkout(name);
                      }}
                    >
                      {name}
                    </li>
                  )}
                </For>
              </ul>
            </Show>
          </div>
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
            <Show
              when={matchedExisting()}
              fallback={
                <>
                  <button
                    type="button"
                    class="session-prompt-btn"
                    onClick={() => create("main")}
                    title="Branch off main (Shift+Enter)"
                  >
                    <span class="session-prompt-btn-label">Off main</span>
                    <span class="session-prompt-btn-key">Shift+Enter</span>
                  </button>
                  <button
                    type="button"
                    class="session-prompt-btn session-prompt-btn-primary"
                    onClick={() => create("head")}
                    title="Branch off HEAD (Enter)"
                  >
                    <span class="session-prompt-btn-label">Off HEAD</span>
                    <span class="session-prompt-btn-key">Enter</span>
                  </button>
                </>
              }
            >
              <button
                type="button"
                class="session-prompt-btn session-prompt-btn-primary"
                onClick={() => checkout(trimmed())}
                title={`Check out existing branch ${trimmed()} (Enter)`}
              >
                <span class="session-prompt-btn-label">Check out {trimmed()}</span>
                <span class="session-prompt-btn-key">Enter</span>
              </button>
            </Show>
          </div>
        </div>
      </div>
    </Portal>
  );
}
