import type { JSX } from "solid-js";
import { ModalShell, modalSubmitKeys, PromptButton } from "../chrome/ModalShell";

/**
 * Portaled modal confirm dialog. A capture-phase listener handles Enter/Escape so the global keybinding
 * resolver and editor never see those keys while it's up.
 */
export function ConfirmDialog(props: {
  title: string;
  destructive?: boolean;
  body: JSX.Element;
  confirmLabel: string;
  cancelLabel: string;
  onConfirm: () => void;
  onCancel: () => void;
}): JSX.Element {
  const submitKeys = modalSubmitKeys(props.onConfirm, props.onCancel);
  const onKeyDown = (event: KeyboardEvent): void => {
    if (props.destructive && event.key === "Enter") event.stopPropagation();
    else submitKeys(event);
  };
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
            ref={(el) => {
              if (props.destructive) queueMicrotask(() => el.focus());
            }}
            onClick={props.onCancel}
          />
          <PromptButton
            label={props.confirmLabel}
            shortcut={props.destructive ? "" : "Enter"}
            title={props.destructive ? props.confirmLabel : `${props.confirmLabel} (Enter)`}
            onClick={props.onConfirm}
            primary={!props.destructive}
            danger={props.destructive === true}
            class="confirm-btn-primary"
            ref={(el) => {
              // Focus the primary action so Enter/Space act on it immediately.
              if (!props.destructive) queueMicrotask(() => el.focus());
            }}
          />
        </>
      }
    >
      <div class="confirm-body">{props.body}</div>
    </ModalShell>
  );
}
