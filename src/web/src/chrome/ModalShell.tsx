import { createSignal, createUniqueId, type JSX, onCleanup, onMount, Show } from "solid-js";
import { Portal } from "solid-js/web";
import { requestModal } from "./modal-state";
import "../editor/confirm-dialog.css";

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

// labelledBy: the surface draws its own display heading (Getting Started).
type ModalHeading = { title: JSX.Element } | { labelledBy: string };

export function ModalShell(
  props: ModalHeading & {
    children: JSX.Element;
    actions?: JSX.Element;
    onDismiss: () => void;
    class?: string;
    onKeyDown?: (event: KeyboardEvent) => void;
  },
): JSX.Element {
  const onKeyDown = props.onKeyDown;
  const titleId = createUniqueId();
  const [active, setActive] = createSignal(false);
  let cancelModal = (): void => {};
  onMount(() => {
    cancelModal = requestModal(() => {
      setActive(true);
      if (onKeyDown !== undefined) {
        window.addEventListener("keydown", onKeyDown, { capture: true });
      }
    });
  });
  onCleanup(() => {
    if (onKeyDown !== undefined) {
      window.removeEventListener("keydown", onKeyDown, { capture: true });
    }
    cancelModal();
  });

  return (
    <Portal>
      <Show when={active()}>
        <div class="modal-backdrop ui-scrim" onPointerDown={() => props.onDismiss()}>
          <div
            class={`confirm-dialog ui-card${props.class === undefined ? "" : ` ${props.class}`}`}
            role="dialog"
            aria-modal="true"
            aria-labelledby={"labelledBy" in props ? props.labelledBy : titleId}
            onPointerDown={(event) => event.stopPropagation()}
          >
            <Show when={"title" in props && props}>
              {(heading) => (
                <h2 class="ui-section-label ui-card-tag" id={titleId}>
                  {heading().title}
                </h2>
              )}
            </Show>
            {props.children}
            <Show when={"actions" in props}>
              <footer class="confirm-actions">{props.actions}</footer>
            </Show>
          </div>
        </div>
      </Show>
    </Portal>
  );
}

const KEYCAPS: Record<string, string> = { Enter: "↵", Esc: "esc" };

export function PromptButton(props: {
  label: JSX.Element;
  shortcut: string; // "" renders no hint
  title: string;
  onClick: () => void;
  primary?: boolean;
  danger?: boolean;
  disabled?: boolean;
  class?: string;
  ref?: (element: HTMLButtonElement) => void;
}): JSX.Element {
  return (
    <button
      ref={props.ref}
      type="button"
      class={`ui-button ui-button-lg${props.primary === true ? " ui-button-primary" : ""}${props.danger === true ? " ui-button-danger" : ""}${props.class === undefined ? "" : ` ${props.class}`}`}
      disabled={props.disabled}
      onClick={() => props.onClick()}
      title={props.title}
    >
      {props.label}
      <Show when={props.shortcut !== ""}>
        <span class="ui-kbd ui-kbd-cap" aria-hidden="true">
          {KEYCAPS[props.shortcut] ?? props.shortcut}
        </span>
      </Show>
    </button>
  );
}
