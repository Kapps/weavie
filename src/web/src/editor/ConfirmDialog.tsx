import type { JSX } from "solid-js";
import { ModalShell, modalSubmitKeys } from "../chrome/ModalShell";

/**
 * Portaled modal confirm dialog. A capture-phase listener handles Enter/Escape so the global keybinding
 * resolver and editor never see those keys while it's up.
 */
export function ConfirmDialog(props: {
  title: string;
  body: JSX.Element;
  confirmLabel: string;
  cancelLabel: string;
  onConfirm: () => void;
  onCancel: () => void;
}): JSX.Element {
  const onKeyDown = modalSubmitKeys(props.onConfirm, props.onCancel);
  return (
    <ModalShell labelledBy="confirm-dialog-title" onDismiss={props.onCancel} onKeyDown={onKeyDown}>
      <div class="confirm-title" id="confirm-dialog-title">
        {props.title}
      </div>
      <div class="confirm-body">{props.body}</div>
      <div class="confirm-actions">
        <button type="button" class="confirm-btn" onClick={() => props.onCancel()}>
          {props.cancelLabel}
        </button>
        <button
          type="button"
          class="confirm-btn confirm-btn-primary"
          ref={(el) => {
            // Focus the primary action so Enter/Space act on it immediately.
            queueMicrotask(() => el.focus());
          }}
          onClick={() => props.onConfirm()}
        >
          {props.confirmLabel}
        </button>
      </div>
    </ModalShell>
  );
}
