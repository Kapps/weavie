import type { Toast } from "./Toasts";

// A module-level channel so any subsystem (e.g. the LSP client) can raise a user-facing toast without threading
// a callback through it. App owns the toast list and registers the sink at mount; before then there's no UI to
// show, so notifications are dropped.
type NotifySink = (level: Toast["level"], message: string) => void;
let sink: NotifySink | null = null;

/** Registers the toast sink. App calls this once at mount. */
export function setNotifySink(next: NotifySink): void {
  sink = next;
}

/** Surfaces a user-facing notification from anywhere in the web app — no host round-trip. */
export function notify(level: Toast["level"], message: string): void {
  sink?.(level, message);
}
