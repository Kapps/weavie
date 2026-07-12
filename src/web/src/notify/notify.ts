import type { Toast, ToastAction } from "./Toasts";

// A module-level channel so any subsystem (e.g. the LSP client) can raise a user-facing toast without threading
// a callback through it. App owns the toast list and registers the sink at mount; before then there's no UI to
// show, so notifications are dropped.
type NotifySink = (
  level: Toast["level"],
  message: string,
  key?: string,
  action?: ToastAction,
) => void;
let sink: NotifySink | null = null;

/** Registers the toast sink. App calls this once at mount. */
export function setNotifySink(next: NotifySink): void {
  sink = next;
}

/**
 * Surfaces a user-facing notification from anywhere in the web app — no host round-trip. An optional `key`
 * dedupes: a later toast with the same key replaces the live one in place (e.g. "Reconnected" clearing the
 * lingering "Lost connection" error) rather than stacking a second row. An optional `action` renders a
 * button whose click (a user gesture) runs it and dismisses the toast.
 */
export function notify(
  level: Toast["level"],
  message: string,
  key?: string,
  action?: ToastAction,
): void {
  sink?.(level, message, key, action);
}
