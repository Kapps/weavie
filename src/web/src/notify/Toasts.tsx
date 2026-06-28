import { For, type JSX, createSignal } from "solid-js";

/** One transient notification surfaced to the user (e.g. an autosave write that failed). */
export interface Toast {
  id: number;
  level: "error" | "warn" | "info";
  message: string;
  // Optional dedupe key: a new toast with the same key replaces the live one in place (e.g. a "Reconnected"
  // info replacing the lingering "Lost connection" error), instead of stacking a second row.
  key?: string;
}

/** How long a non-error toast lingers before auto-dismissing. Errors are exempt — see addToast. */
const AUTO_DISMISS_MS = 6000;
// How long the collapse-out animation runs before the toast is actually removed. Keep in sync with the
// `.toast.leaving` transition in notify.css so the row is gone exactly when its animation finishes.
const EXIT_MS = 200;

/**
 * Toast list state + add/dismiss helpers. Errors persist until dismissed; warn/info auto-dismiss. A dismiss
 * marks the toast `leaving` (so it animates out) and removes it after the exit animation, rather than yanking
 * it — which would snap the rest of the stack up. Each pending timer is tracked by id so a manual dismiss cancels it.
 */
export function createToasts(): {
  toasts: () => Toast[];
  addToast: (level: Toast["level"], message: string, key?: string) => void;
  dismissToast: (id: number) => void;
  isLeaving: (id: number) => boolean;
} {
  const [toasts, setToasts] = createSignal<Toast[]>([]);
  const [leaving, setLeaving] = createSignal<Set<number>>(new Set());
  const timers = new Map<number, number>();
  let nextId = 0;
  const remove = (id: number): void => {
    setToasts((list) => list.filter((t) => t.id !== id));
    setLeaving((prev) => {
      if (!prev.has(id)) {
        return prev;
      }
      const next = new Set(prev);
      next.delete(id);
      return next;
    });
  };
  const dismissToast = (id: number): void => {
    const timer = timers.get(id);
    if (timer !== undefined) {
      window.clearTimeout(timer);
      timers.delete(id);
    }
    if (leaving().has(id)) {
      return; // already animating out
    }
    setLeaving((prev) => new Set(prev).add(id));
    window.setTimeout(() => remove(id), EXIT_MS);
  };
  // Re-arms (or clears) a toast's auto-dismiss: errors persist until dismissed; everything else clears itself.
  const armAutoDismiss = (id: number, level: Toast["level"]): void => {
    const existing = timers.get(id);
    if (existing !== undefined) {
      window.clearTimeout(existing);
      timers.delete(id);
    }
    if (level !== "error") {
      timers.set(
        id,
        window.setTimeout(() => dismissToast(id), AUTO_DISMISS_MS),
      );
    }
  };
  const addToast = (level: Toast["level"], message: string, key?: string): void => {
    // A keyed toast replaces the live one with the same key in place — e.g. the "Reconnected" info supersedes
    // the lingering "Lost connection" error, so a resolved condition never leaves a stale toast on screen.
    if (key !== undefined) {
      const current = toasts().find((t) => t.key === key && !leaving().has(t.id));
      if (current !== undefined) {
        setToasts((list) => list.map((t) => (t.id === current.id ? { ...t, level, message } : t)));
        armAutoDismiss(current.id, level);
        return;
      }
    }
    const id = ++nextId;
    const toast: Toast = key === undefined ? { id, level, message } : { id, level, message, key };
    setToasts((list) => [...list, toast]);
    armAutoDismiss(id, level);
  };
  return { toasts, addToast, dismissToast, isLeaving: (id) => leaving().has(id) };
}

// A top-center stack of dismissible toasts below the title bar. Self-contained (no editor/layout coupling)
// so it can overlay any pane.
export function Toasts(props: {
  toasts: Toast[];
  onDismiss: (id: number) => void;
  isLeaving: (id: number) => boolean;
}): JSX.Element {
  return (
    <div class="toasts">
      <For each={props.toasts}>
        {(toast) => (
          <div
            class={`toast toast-${toast.level}`}
            classList={{ leaving: props.isLeaving(toast.id) }}
            role="alert"
          >
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
