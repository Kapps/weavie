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
// Timed toasts auto-dismiss and show the drain fill; errors persist until dismissed.
const isTimed = (level: Toast["level"]): boolean => level !== "error";
// How long the collapse-out animation runs before the toast is actually removed. Keep in sync with the
// `.toast.leaving` transition in notify.css so the row is gone exactly when its animation finishes.
const EXIT_MS = 200;

/**
 * Toast list state + add/dismiss helpers. Errors persist until dismissed; warn/info auto-dismiss. A dismiss
 * marks the toast `leaving` (so it animates out) and removes it after the exit animation, rather than yanking
 * it — which would snap the rest of the stack up. Each pending timer is tracked by id so a manual dismiss cancels it.
 * Hovering a timed toast pauses its clock (pauseToast/resumeToast) so the user can read or copy from it.
 */
export function createToasts(): {
  toasts: () => Toast[];
  addToast: (level: Toast["level"], message: string, key?: string) => void;
  dismissToast: (id: number) => void;
  isLeaving: (id: number) => boolean;
  pauseToast: (id: number) => void;
  resumeToast: (id: number) => void;
} {
  const [toasts, setToasts] = createSignal<Toast[]>([]);
  const [leaving, setLeaving] = createSignal<Set<number>>(new Set());
  const timers = new Map<number, { handle: number; expiresAt: number }>();
  const paused = new Map<number, number>(); // id -> remaining ms while hovered
  // Hover is tracked separately from `paused` (which only exists for running timers) so a hovered error
  // toast replaced in place by a timed one stays paused — the pointer is still on it.
  const hovered = new Set<number>();
  let nextId = 0;
  const remove = (id: number): void => {
    hovered.delete(id);
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
  const disarm = (id: number): void => {
    const timer = timers.get(id);
    if (timer !== undefined) {
      window.clearTimeout(timer.handle);
      timers.delete(id);
    }
    paused.delete(id);
  };
  const dismissToast = (id: number): void => {
    disarm(id);
    if (leaving().has(id)) {
      return; // already animating out
    }
    setLeaving((prev) => new Set(prev).add(id));
    window.setTimeout(() => remove(id), EXIT_MS);
  };
  const startTimer = (id: number, ms: number): void => {
    timers.set(id, {
      handle: window.setTimeout(() => dismissToast(id), ms),
      expiresAt: Date.now() + ms,
    });
  };
  // Re-arms (or clears) a toast's auto-dismiss: errors persist until dismissed; everything else clears itself.
  // A keyed replacement while hovered stays paused (the pointer is still on it) with a fresh full duration.
  const armAutoDismiss = (id: number, level: Toast["level"]): void => {
    disarm(id);
    if (!isTimed(level)) {
      return;
    }
    if (hovered.has(id)) {
      paused.set(id, AUTO_DISMISS_MS);
      return;
    }
    startTimer(id, AUTO_DISMISS_MS);
  };
  const pauseToast = (id: number): void => {
    hovered.add(id);
    const timer = timers.get(id);
    if (timer === undefined) {
      return; // no timer running: error toast, already paused, or already leaving
    }
    window.clearTimeout(timer.handle);
    timers.delete(id);
    paused.set(id, Math.max(0, timer.expiresAt - Date.now()));
  };
  const resumeToast = (id: number): void => {
    hovered.delete(id);
    const remaining = paused.get(id);
    if (remaining === undefined) {
      return;
    }
    paused.delete(id);
    startTimer(id, remaining);
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
  return {
    toasts,
    addToast,
    dismissToast,
    isLeaving: (id) => leaving().has(id),
    pauseToast,
    resumeToast,
  };
}

// A top-center stack of dismissible toasts below the title bar. Self-contained (no editor/layout coupling)
// so it can overlay any pane. Timed toasts drain a background fill over their lifetime (notify.css) and
// pause it — plus the dismiss timer — while hovered, so the content can be read or copied without a race.
export function Toasts(props: {
  toasts: Toast[];
  onDismiss: (id: number) => void;
  isLeaving: (id: number) => boolean;
  onPause: (id: number) => void;
  onResume: (id: number) => void;
}): JSX.Element {
  return (
    <div class="toasts">
      <For each={props.toasts}>
        {(toast) => (
          <div
            class={`toast toast-${toast.level}`}
            classList={{ leaving: props.isLeaving(toast.id), "toast-timed": isTimed(toast.level) }}
            style={`--toast-duration:${AUTO_DISMISS_MS}ms`}
            role="alert"
            onMouseEnter={() => props.onPause(toast.id)}
            onMouseLeave={() => props.onResume(toast.id)}
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
