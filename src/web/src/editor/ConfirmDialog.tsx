import { type JSX, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";

/**
 * Portaled modal confirm dialog. Enter confirms, Escape cancels via a capture-phase listener so the global
 * keybinding resolver and editor never see those keys while it's up.
 */
export function ConfirmDialog(props: {
  title: string;
  body: JSX.Element;
  confirmLabel: string;
  cancelLabel: string;
  onConfirm: () => void;
  onCancel: () => void;
}): JSX.Element {
  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Enter") {
      event.preventDefault();
      event.stopPropagation();
      props.onConfirm();
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
        {/* Stop backdrop dismissal when interacting with the dialog itself. */}
        <div class="confirm-dialog" onPointerDown={(event) => event.stopPropagation()}>
          <div class="confirm-title">{props.title}</div>
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
        </div>
      </div>
    </Portal>
  );
}
