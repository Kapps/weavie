import { createEffect, createSignal, For, type JSX, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { connectedBackends, requestBranches } from "../bridge";
import { BranchTypeahead, branchSuggestions } from "./BranchTypeahead";

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
  const [loadingBranches, setLoadingBranches] = createSignal(false);

  // If the picked location disconnects while the prompt is open, fall back to local so a create/checkout
  // can't silently post into a dead backend (postToBackend would no-op).
  createEffect(() => {
    if (!connectedBackends().some((b) => b.id === backendId())) {
      setBackendId("local");
    }
  });

  // Load the chosen backend's branches, reloading on location change; ignore a stale reply from a location
  // the user has since switched away from.
  createEffect(() => {
    const id = backendId();
    setBranches([]);
    setLoadingBranches(true);
    void requestBranches(id).then((list) => {
      if (backendId() === id) {
        setBranches(list);
        setLoadingBranches(false);
      }
    });
  });

  // The display name of the currently-picked location ("" for local), for the Disconnect affordance.
  const selectedRemoteName = (): string =>
    connectedBackends().find((b) => b.id === backendId() && !b.isLocal)?.name ?? "";

  const trimmed = (): string => branch().trim();
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

  return (
    <Portal>
      <div class="modal-backdrop" onPointerDown={() => props.onCancel()}>
        <div
          class="confirm-dialog session-prompt"
          role="dialog"
          aria-modal="true"
          aria-labelledby="new-session-title"
          onPointerDown={(event) => event.stopPropagation()}
        >
          <div class="confirm-title" id="new-session-title">
            New session
          </div>
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
            <BranchTypeahead
              idPrefix="session-branch"
              placeholder="branch name"
              ariaLabel="Branch name"
              branches={branches()}
              value={branch()}
              setValue={setBranch}
              onSubmit={(text, shiftKey, viaPick) => {
                if (viaPick || branches().includes(text)) {
                  checkout(text); // an existing branch (picked or typed in full) → check it out
                } else {
                  create(shiftKey ? "main" : "head"); // a new branch name → create it
                }
              }}
              onCancel={() => props.onCancel()}
            />
            {/* While branches load, say so — otherwise a typed name that matches an existing branch looks
                like a new branch (no suggestion yet), and a hung backend looks identical to an empty repo. */}
            <Show
              when={
                loadingBranches() &&
                trimmed().length > 0 &&
                branchSuggestions(branches(), branch()).length === 0
              }
            >
              <div class="session-prompt-hint">Loading branches…</div>
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
