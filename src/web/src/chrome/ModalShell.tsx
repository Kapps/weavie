import { type JSX, onCleanup, onMount } from "solid-js";
import { Portal } from "solid-js/web";

export function modalSubmitKeys(
  onSubmit: () => void,
  onDismiss: () => void,
): (event: KeyboardEvent) => void {
  return (event) => {
    const action =
      event.key === "Enter" ? onSubmit : event.key === "Escape" ? onDismiss : undefined;
    if (action !== undefined) {
      event.preventDefault();
      event.stopPropagation();
      action();
    }
  };
}

export function ModalShell(props: {
  children: JSX.Element;
  labelledBy: string;
  onDismiss: () => void;
  class?: string;
  onKeyDown?: (event: KeyboardEvent) => void;
}): JSX.Element {
  const onKeyDown = props.onKeyDown;
  onMount(() => {
    if (onKeyDown !== undefined) {
      window.addEventListener("keydown", onKeyDown, { capture: true });
    }
  });
  onCleanup(() => {
    if (onKeyDown !== undefined) {
      window.removeEventListener("keydown", onKeyDown, { capture: true });
    }
  });

  return (
    <Portal>
      <div class="modal-backdrop" onPointerDown={() => props.onDismiss()}>
        <div
          class={`confirm-dialog${props.class === undefined ? "" : ` ${props.class}`}`}
          role="dialog"
          aria-modal="true"
          aria-labelledby={props.labelledBy}
          onPointerDown={(event) => event.stopPropagation()}
        >
          {props.children}
        </div>
      </div>
    </Portal>
  );
}

export function PromptActions(props: { children: JSX.Element }): JSX.Element {
  return <div class="session-prompt-actions">{props.children}</div>;
}

export function PromptButton(props: {
  label: JSX.Element;
  shortcut: string;
  title: string;
  onClick: () => void;
  primary?: boolean;
  disabled?: boolean;
}): JSX.Element {
  return (
    <button
      type="button"
      class={`session-prompt-btn${props.primary === true ? " session-prompt-btn-primary" : ""}`}
      disabled={props.disabled}
      onClick={() => props.onClick()}
      title={props.title}
    >
      <span class="session-prompt-btn-label">{props.label}</span>
      <span class="session-prompt-btn-key">{props.shortcut}</span>
    </button>
  );
}
