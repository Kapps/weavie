import { createSignal, For, type JSX, Show } from "solid-js";
import { ModalShell, modalSubmitKeys } from "./ModalShell";

// How dirty the session's worktree is (host git status), driving the confirm friction: clean = one click,
// untracked = two-step confirm, modified = checkbox acknowledgement.
export type DeleteSessionState = "clean" | "untracked" | "modified";

/**
 * The session-delete confirm: deleting removes the worktree (branch always kept), so friction escalates with
 * its state (see DeleteSessionState). Enter confirms when allowed, Esc cancels, via a capture-phase listener
 * so the global keybinding resolver never sees those keys.
 */
export function DeleteSessionDialog(props: {
  label: string;
  state: DeleteSessionState;
  // The first few uncommitted paths a delete would discard, plus their total.
  changedFiles: string[];
  changedCount: number;
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

  const onKeyDown = modalSubmitKeys(confirm, props.onCancel);
  const confirmLabel = (): string => {
    if (props.state === "untracked") {
      return armed() ? "Confirm delete" : "Delete untracked files…";
    }
    return "Delete session";
  };

  return (
    <ModalShell labelledBy="delete-session-title" onDismiss={props.onCancel} onKeyDown={onKeyDown}>
      <div class="confirm-title" id="delete-session-title">
        Delete session?
      </div>
      <div class="confirm-body">
        <Show when={props.state === "clean"}>
          Remove the worktree for "{props.label}"? The branch is kept, so committed work is safe and
          you can recreate a session on it later.
        </Show>
        <Show when={props.state === "untracked"}>
          <div>
            Removing the worktree for "{props.label}" also deletes its{" "}
            <strong>untracked files</strong> — they aren't committed, so they can't be recovered.
            The branch is kept.
          </div>
        </Show>
        <Show when={props.state === "modified"}>
          <div>
            "{props.label}" has <strong>uncommitted changes</strong> that will be permanently lost
            when its worktree is removed. The branch keeps only committed work.
          </div>
        </Show>
        <Show when={props.state !== "clean" && props.changedFiles.length > 0}>
          <ul class="confirm-file-list">
            <For each={props.changedFiles}>{(file) => <li>{file}</li>}</For>
            <Show when={props.changedCount > props.changedFiles.length}>
              <li class="confirm-file-more">
                …and {props.changedCount - props.changedFiles.length} more
              </li>
            </Show>
          </ul>
        </Show>
        <Show when={props.state === "untracked" && armed()}>
          <div class="confirm-warn">
            Click confirm to delete the worktree and its untracked files.
          </div>
        </Show>
        <Show when={props.state === "modified"}>
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
    </ModalShell>
  );
}
