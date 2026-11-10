import type { JSX } from "solid-js";
import { ModalShell, modalSubmitKeys, PromptButton } from "../chrome/ModalShell";

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
    <ModalShell
      title={props.title}
      onDismiss={props.onCancel}
      onKeyDown={onKeyDown}
      actions={
        <>
          <PromptButton
            label={props.cancelLabel}
            shortcut="Esc"
            title={`${props.cancelLabel} (Esc)`}
            onClick={props.onCancel}
          />
          <PromptButton
            label={props.confirmLabel}
            shortcut="Enter"
            title={`${props.confirmLabel} (Enter)`}
            onClick={props.onConfirm}
            primary
            class="confirm-btn-primary"
            ref={(el) => {
              // Focus the primary action so Enter/Space act on it immediately.
              queueMicrotask(() => el.focus());
            }}
          />
        </>
      }
    >
      <div class="confirm-body">{props.body}</div>
    </ModalShell>
  );
}
