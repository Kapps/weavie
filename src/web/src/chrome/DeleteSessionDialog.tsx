import { type JSX, Show, createSignal, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";

// How dirty the session's worktree is, classified by the host (git status). Drives how hard the dialog makes
// the user work to confirm: clean = one click; untracked = a two-step confirm; modified = a checkbox
// acknowledgement.
export type DeleteSessionState = "clean" | "untracked" | "modified";

/**
 * The session-delete confirm. Deleting removes the worktree (the branch is always kept), discarding anything
 * uncommitted, so the dialog escalates with the worktree's state: plain confirm when clean, two-step confirm
 * for untracked files, a checkbox when tracked changes would be lost. Enter confirms when allowed, Esc
 * cancels, via a capture-phase listener so the global keybinding resolver never sees those keys.
 */
export function DeleteSessionDialog(props: {
  label: string;
  state: DeleteSessionState;
  onConfirm: () => void;
  onCancel: () => void;
}): JSX.Element {
  // untracked: armed by the first click, confirmed by the second. modified: gated on the acknowledgement box.
  const [armed, setArmed] = createSignal(false);
  const [acknowledged, setAcknowledged] = createSignal(false);

  const canConfirm = (): boolean => props.state !== "modified" || acknowledged();

  const confirm = (): void => {
    if (props.state === "untracked" && !armed()) {
      setArmed(true);
      return;
    }
    if (canConfirm()) {
      props.onConfirm();
    }
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Enter") {
      event.preventDefault();
      event.stopPropagation();
      confirm();
    } else if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      props.onCancel();
    }
  };
  onMount(() => window.addEventListener("keydown", onKeyDown, { capture: true }));
  onCleanup(() => window.removeEventListener("keydown", onKeyDown, { capture: true }));

  const confirmLabel = (): string => {
    if (props.state === "untracked") {
      return armed() ? "Confirm delete" : "Delete untracked files…";
    }
    return "Delete session";
  };

  return (
    <Portal>
      <div class="modal-backdrop" onPointerDown={() => props.onCancel()}>
        <div class="confirm-dialog" onPointerDown={(event) => event.stopPropagation()}>
          <div class="confirm-title">Delete session?</div>
          <div class="confirm-body">
            <Show when={props.state === "clean"}>
              Remove the worktree for "{props.label}"? The branch is kept, so committed work is safe
              and you can recreate a session on it later.
            </Show>
            <Show when={props.state === "untracked"}>
              <div>
                Removing the worktree for "{props.label}" also deletes its{" "}
                <strong>untracked files</strong> — they aren't committed, so they can't be
                recovered. The branch is kept.
              </div>
              <Show when={armed()}>
                <div class="confirm-warn">
                  Click confirm to delete the worktree and its untracked files.
                </div>
              </Show>
            </Show>
            <Show when={props.state === "modified"}>
              <div>
                "{props.label}" has <strong>uncommitted changes</strong> that will be permanently
                lost when its worktree is removed. The branch keeps only committed work.
              </div>
              <label class="confirm-check">
                <input
                  type="checkbox"
                  checked={acknowledged()}
                  onChange={(event) => setAcknowledged(event.currentTarget.checked)}
                  ref={(el) => {
                    if (props.state === "modified") {
                      queueMicrotask(() => el.focus());
                    }
                  }}
                />
                <span>I understand all uncommitted changes will be removed</span>
              </label>
            </Show>
          </div>
          <div class="confirm-actions">
            <button type="button" class="confirm-btn" onClick={() => props.onCancel()}>
              Cancel
            </button>
            <button
              type="button"
              class="confirm-btn confirm-btn-danger"
              disabled={!canConfirm()}
              ref={(el) => {
                if (props.state !== "modified") {
                  queueMicrotask(() => el.focus());
                }
              }}
              onClick={() => confirm()}
            >
              {confirmLabel()}
            </button>
          </div>
        </div>
      </div>
    </Portal>
  );
}
