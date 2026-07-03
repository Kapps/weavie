import { createSignal } from "solid-js";
import { onHostMessage } from "../bridge";
import { notify } from "../notify/notify";

/** One thing holding a pending update restart (a busy session or a running shell job). */
export type UpdateHold = { session: string; reason: "working" | "needs-input" | "shell-job" };

// The reloaded page's only memory that an update just landed (set right before the forced reload).
const UPDATED_KEY = "weavie-updated-to";

// Active-backend drain state (top-level signals so they survive HMR). `holds` is null when no update
// is pending; `restarting` flips on the commit push and drives the blocking overlay.
const [holds, setHolds] = createSignal<UpdateHold[] | null>(null);
const [restarting, setRestarting] = createSignal(false);

onHostMessage((message) => {
  if (message.type === "update-pending") {
    setHolds(message.holds);
    setRestarting(false);
  } else if (message.type === "update-restarting") {
    setRestarting(true);
  } else if (message.type === "host-info") {
    // A fresh ready cycle. Stale tab (the worker was updated under us) → reload to get the matching
    // assets; otherwise clear drain state the (re)connected worker no longer has. Dev builds all
    // stamp the same number and plain-browser dev has no shell global, so neither ever reloads here.
    const boot = window.__WEAVIE_SHELL__?.buildNumber;
    if (boot !== undefined && boot !== "" && message.buildNumber !== boot) {
      window.sessionStorage.setItem(UPDATED_KEY, message.buildNumber);
      window.location.reload();
      return;
    }
    if (restarting()) {
      // We were told a restart was applying an update, but the worker came back on the same build —
      // it was rolled back (or re-served the old build). The user must hear that, not infer it.
      notify(
        "warn",
        "The update didn't apply — the worker is back on the same build (it may have been rolled back). Check the runner page.",
      );
    }
    setHolds(null);
    setRestarting(false);
  }
});

/**
 * Surfaces the "updated to build N" notice after the forced reload — the fresh page has no other
 * memory of it. Called by App once the toast sink is registered (module eval is too early to toast).
 */
export function surfacePostUpdateNotice(): void {
  const updatedTo = window.sessionStorage.getItem(UPDATED_KEY);
  if (updatedTo !== null) {
    window.sessionStorage.removeItem(UPDATED_KEY);
    notify("info", `Weavie updated to build ${updatedTo}.`);
  }
}

/** What's holding the pending update, or null when no update is pending. */
export const updateHolds = holds;

/** True from the restart commit until the new worker's first ready cycle (or a reload). */
export const updateRestarting = restarting;
