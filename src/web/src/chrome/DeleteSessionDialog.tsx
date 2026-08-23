import { createSignal, For, type JSX, Show } from "solid-js";
import { ModalShell, modalSubmitKeys } from "./ModalShell";

// How dirty the session's worktree is (host git status), driving the confirm friction: clean = one click,
// untracked = two-step confirm, modified = checkbox acknowledgement.
export type DeleteSessionState = "clean" | "untracked" | "modified";

/**
 * The session-delete confirm. Worktree removal escalates with the checkout's state; deleting the session on the
 * workspace's own checkout removes only the session. Enter confirms when allowed and Esc cancels.
 */
export function DeleteSessionDialog(props: {
  label: string;
  removesCheckout: boolean;
  state: DeleteSessionState;
  // The directory the removal deletes, shown so a confirm names what it destroys.
  worktreePath: string;
  // Nothing keeps this checkout's commits — detached, or a worktree git no longer reports.
  branchless: boolean;
  // The first few uncommitted paths a delete would discard, plus their total.
  changedFiles: string[];
  changedCount: number;
  onConfirm: () => void;
  onCancel: () => void;
}): JSX.Element {
  // untracked: armed by the first click, confirmed by the second. modified: gated on the acknowledgement box.
  const [armed, setArmed] = createSignal(false);
  const [acknowledged, setAcknowledged] = createSignal(false);

  // Losing commits is as unrecoverable as losing uncommitted changes, so both are gated on the checkbox.
  const needsAcknowledgement = (): boolean => props.state === "modified" || props.branchless;
  const canConfirm = (): boolean => !needsAcknowledgement() || acknowledged();

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
          <Show
            when={props.removesCheckout}
            fallback={<>Remove session "{props.label}"? Its checkout and files remain on disk.</>}
          >
            <Show
              when={props.branchless}
              fallback={
                <>
                  Remove the worktree for "{props.label}"? Its directory and everything in it,
                  including ignored files, is deleted. The branch is kept, so committed work is
                  safe.
                </>
              }
            >
              Remove the worktree for "{props.label}"? It has <strong>no branch</strong>, so the
              commits made here are lost along with its directory and everything in it.
            </Show>
          </Show>
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
        <Show when={props.branchless && props.state !== "clean"}>
          <div class="confirm-warn">
            This checkout has <strong>no branch</strong>, so its commits are lost too.
          </div>
        </Show>
        <Show when={props.removesCheckout}>
          <div class="confirm-path">{props.worktreePath}</div>
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
        <Show when={needsAcknowledgement()}>
          <label class="confirm-check">
            <input
              type="checkbox"
              checked={acknowledged()}
              onChange={(event) => setAcknowledged(event.currentTarget.checked)}
              ref={(el) => {
                if (needsAcknowledgement()) {
                  queueMicrotask(() => el.focus());
                }
              }}
            />
            <span>
              {props.branchless
                ? "I understand the commits made in this checkout will be lost"
                : "I understand all uncommitted changes will be removed"}
            </span>
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
            if (!needsAcknowledgement()) {
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
