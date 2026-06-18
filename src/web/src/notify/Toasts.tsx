import { For, type JSX } from "solid-js";

/** One transient notification surfaced to the user (e.g. an autosave write that failed). */
export interface Toast {
  id: number;
  level: "error" | "warn" | "info";
  message: string;
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
