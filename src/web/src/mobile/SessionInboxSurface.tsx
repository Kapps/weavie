import { X } from "lucide-solid";
import { createEffect, createSignal, type JSX, onCleanup } from "solid-js";
import { modalActive, requestModal } from "../chrome/modal-state";

const FOCUSABLE =
  'button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), [tabindex]:not([tabindex="-1"])';

/** One persistent Sessions surface: a modal dialog on desktop and layout-transparent chrome on mobile. */
export function SessionInboxSurface(props: {
  children: JSX.Element;
  compact: boolean;
  modalOpen: boolean;
  onDismiss: () => void;
}): JSX.Element {
  let dialog!: HTMLDivElement;
  let restoreFocus: HTMLElement | null = null;
  let listening = false;
  let wasOpen = false;
  let generation = 0;
  const [modalClaimed, setModalClaimed] = createSignal(false);
  const desktopRequested = (): boolean => props.modalOpen && !props.compact;
  const desktopOpen = (): boolean => desktopRequested() && modalClaimed();

  const focusable = (): HTMLElement[] =>
    [...dialog.querySelectorAll<HTMLElement>(FOCUSABLE)].filter(
      (element) => element.offsetParent !== null,
    );

  const onKeyDown = (event: KeyboardEvent): void => {
    if (!desktopOpen()) {
      return;
    }
    if (event.key === "Escape") {
      event.preventDefault();
      event.stopPropagation();
      props.onDismiss();
      return;
    }
    if (event.key !== "Tab") {
      return;
    }
    const controls = focusable();
    if (controls.length === 0) {
      event.preventDefault();
      dialog.focus();
      return;
    }
    const first = controls[0]!;
    const last = controls[controls.length - 1]!;
    const active = document.activeElement;
    if (!dialog.contains(active) || (event.shiftKey ? active === first : active === last)) {
      event.preventDefault();
      (event.shiftKey ? last : first).focus();
    }
  };

  const stopListening = (): void => {
    if (listening) {
      window.removeEventListener("keydown", onKeyDown, { capture: true });
      listening = false;
    }
  };

  createEffect(() => {
    if (!desktopRequested()) {
      return;
    }
    const cancelModal = requestModal(() => setModalClaimed(true));
    onCleanup(() => {
      setModalClaimed(false);
      cancelModal();
    });
  });

  createEffect(() => {
    const open = desktopOpen();
    if (open) {
      dialog.setAttribute("role", "dialog");
      dialog.setAttribute("aria-modal", "true");
      dialog.setAttribute("aria-labelledby", "session-inbox-title");
      dialog.tabIndex = -1;
    } else {
      dialog.removeAttribute("role");
      dialog.removeAttribute("aria-modal");
      dialog.removeAttribute("aria-labelledby");
      dialog.removeAttribute("tabindex");
    }
    if (open === wasOpen) {
      return;
    }
    wasOpen = open;
    const currentGeneration = ++generation;
    if (open) {
      window.addEventListener("keydown", onKeyDown, { capture: true });
      listening = true;
      queueMicrotask(() => {
        if (generation !== currentGeneration || !desktopOpen()) {
          return;
        }
        const active = document.activeElement;
        restoreFocus = active instanceof HTMLElement && active !== document.body ? active : null;
        (
          dialog.querySelector<HTMLElement>('textarea[aria-label="Prompt for a new session"]') ??
          focusable()[0] ??
          dialog
        ).focus();
      });
      return;
    }
    stopListening();
    const target = restoreFocus;
    restoreFocus = null;
    requestAnimationFrame(() => {
      const active = document.activeElement;
      const focusStayedInModal = active === document.body || dialog.contains(active);
      if (
        generation === currentGeneration &&
        !modalActive() &&
        focusStayedInModal &&
        target?.isConnected
      ) {
        target.focus();
      }
    });
  });

  onCleanup(() => {
    ++generation;
    stopListening();
  });

  return (
    <div
      class="session-inbox-surface"
      classList={{ open: desktopOpen() }}
      onPointerDown={(event) => {
        if (event.target === event.currentTarget && desktopOpen()) {
          props.onDismiss();
        }
      }}
    >
      <div class="session-inbox-dialog" ref={dialog}>
        <button
          type="button"
          class="session-inbox-close"
          aria-label="Close Sessions"
          title="Close Sessions (Esc)"
          onClick={() => props.onDismiss()}
        >
          <X size={16} aria-hidden="true" />
        </button>
        {props.children}
      </div>
    </div>
  );
}
