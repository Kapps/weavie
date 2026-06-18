import { For, type JSX, createSignal } from "solid-js";

/** One transient notification surfaced to the user (e.g. an autosave write that failed). */
export interface Toast {
  id: number;
  level: "error" | "warn" | "info";
  message: string;
}

/** Toast list state + helpers: add (auto-dismisses after a few seconds) and manual dismiss. */
export function createToasts(): {
  toasts: () => Toast[];
  addToast: (level: Toast["level"], message: string) => void;
  dismissToast: (id: number) => void;
} {
  const [toasts, setToasts] = createSignal<Toast[]>([]);
  let nextId = 0;
  const dismissToast = (id: number): void => {
    setToasts((list) => list.filter((t) => t.id !== id));
  };
  const addToast = (level: Toast["level"], message: string): void => {
    const id = ++nextId;
    setToasts((list) => [...list, { id, level, message }]);
    window.setTimeout(() => dismissToast(id), 6000);
  };
  return { toasts, addToast, dismissToast };
}

// A bottom-stacked list of dismissible toasts. Self-contained — no editor / layout coupling — so it can
// overlay any pane. Auto-dismiss is owned by the caller (App), which also feeds the list.
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
