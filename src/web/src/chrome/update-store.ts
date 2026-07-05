import { createSignal } from "solid-js";
import { onHostMessage } from "../bridge";
import { notify } from "../notify/notify";

/** One thing holding a pending update restart (a busy session, a running shell job, or a pending task). */
export type UpdateHold = {
  session: string;
  reason: "working" | "needs-input" | "shell-job" | "waiting-on-task";
};

// The reloaded page's only memory that an update just landed (set right before the forced reload).
const UPDATED_KEY = "weavie-updated-to";
// Stable key for the "update ready" toast so a re-pushed pending (reconnect, changed holds) can't stack a row.
const UPDATE_TOAST_KEY = "weavie-update-ready";

// Active-backend drain state (top-level signals so they survive HMR). `holds` is null when no update
// is pending; `restarting` flips on the commit push and drives the blocking overlay; `pending` is the
// episode latch that gates the once-per-episode announce (see `updatePending`).
const [holds, setHolds] = createSignal<UpdateHold[] | null>(null);
const [restarting, setRestarting] = createSignal(false);
const [pending, setPending] = createSignal(false);

onHostMessage((message) => {
  if (message.type === "update-pending") {
    // Announce once per episode; a re-pushed pending (reconnect, changed holds) only refreshes them.
    if (!pending()) {
      setPending(true);
      notify(
        "info",
        "Update ready — it'll apply on its own once your sessions go idle.",
        UPDATE_TOAST_KEY,
      );
    }
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
      // it was rolled back (or re-served the old build). The user must hear that, not infer it. The
      // episode is over, so drop the latch: a future update announces again.
      setPending(false);
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

/**
 * True for the whole update episode — steady across a mid-drain reconnect's transient `holds` clear,
 * unlike `updateHolds`. Drives "start the card collapsed for each new episode" so a reconnect doesn't
 * collapse a card the user opened.
 */
export const updatePending = pending;

/** True from the restart commit until the new worker's first ready cycle (or a reload). */
export const updateRestarting = restarting;
