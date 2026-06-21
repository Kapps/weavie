import { For, type JSX, createSignal } from "solid-js";

/** One transient notification surfaced to the user (e.g. an autosave write that failed). */
export interface Toast {
  id: number;
  level: "error" | "warn" | "info";
  message: string;
}

/** How long a non-error toast lingers before auto-dismissing. Errors are exempt — see addToast. */
const AUTO_DISMISS_MS = 6000;

/**
 * Toast list state + add/dismiss helpers. Errors persist until dismissed (a failure must not scroll past
 * unseen); warn/info auto-dismiss. Each pending timer is tracked by id so a manual dismiss cancels it.
 */
export function createToasts(): {
  toasts: () => Toast[];
  addToast: (level: Toast["level"], message: string) => void;
  dismissToast: (id: number) => void;
} {
  const [toasts, setToasts] = createSignal<Toast[]>([]);
  const timers = new Map<number, number>();
  let nextId = 0;
  const dismissToast = (id: number): void => {
    const timer = timers.get(id);
    if (timer !== undefined) {
      window.clearTimeout(timer);
      timers.delete(id);
    }

    setToasts((list) => list.filter((t) => t.id !== id));
  };
  const addToast = (level: Toast["level"], message: string): void => {
    const id = ++nextId;
    setToasts((list) => [...list, { id, level, message }]);
    // Errors stay put until dismissed; everything else clears itself.
    if (level !== "error") {
      const timer = window.setTimeout(() => dismissToast(id), AUTO_DISMISS_MS);
      timers.set(id, timer);
    }
  };
  return { toasts, addToast, dismissToast };
}

// A top-center stack of dismissible toasts below the title bar. Self-contained (no editor/layout
// coupling) so it can overlay any pane. Every toast has a close button; warn/info also time out on
// their own (see createToasts).
export function Toasts(props: { toasts: Toast[]; onDismiss: (id: number) => void }): JSX.Element {
  return (
    <div class="toasts">
      <For each={props.toasts}>
        {(toast) => (
          <div class={`toast toast-${toast.level}`} role="alert">
            <span class="toast-msg">{toast.message}</span>
            <button
              type="button"
              class="toast-close"
              aria-label="Dismiss"
              onClick={() => props.onDismiss(toast.id)}
            >
              ✕
            </button>
          </div>
        )}
      </For>
    </div>
  );
}
