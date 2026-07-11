// OS-facing presentation for session attention: the Web Notification API (browser-served — the only channel
// that reaches the client's OS from a remote worker) and the tab-title badge. Notifications are raised
// silent (the pack player owns all audio) and tagged per session so repeat pings coalesce; clicking one
// focuses the window and dispatches weavie.session.focus. Permission is never requested cold: the first
// enabled event raises a one-time action toast whose click (a user gesture) triggers the real browser
// prompt. Native webview shells have no path yet (phase 2's host channel) and get one honest toast instead
// of a silent drop. See docs/specs/session-attention.md.

import { type AttentionKindName, isBrowserHostedShell } from "../bridge";
import { dispatchCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";

/** One attention event with the rail identity the notification names and focuses. */
export interface AttentionEvent {
  backendId: string;
  slot: string;
  label: string;
  kind: AttentionKindName;
}

const BODY: Record<AttentionKindName, string> = {
  turnComplete: "Turn complete — waiting on you.",
  needsInput: "Needs your input.",
  failed: "The agent crashed.",
};

// The permission toast fires once per page load and PERSISTS (action toasts never auto-dismiss) — it's
// raised while the user is away, so it must still be there when they come back. A denied prompt is
// remembered by the browser itself; an explicit dismissal holds for the page's lifetime.
let permissionPrompted = false;

/** Raises the OS notification for an attention event (browser path), or the honest capability-gap toast. */
export function presentOsNotification(event: AttentionEvent): void {
  if (!isBrowserHostedShell()) {
    // A native webview shell: no Notification API (WKWebView lacks it entirely); the host-native channel is
    // phase 2. Name the gap where the user is instead of dropping the event silently.
    notify(
      "info",
      "OS notifications aren't supported by this Weavie shell yet — sounds and the session rail still ping.",
      "attention-os-unsupported",
    );
    return;
  }

  if (Notification.permission === "granted") {
    show(event);
    return;
  }

  if (Notification.permission === "default" && !permissionPrompted) {
    permissionPrompted = true;
    notify(
      "info",
      "Weavie can notify you when a session needs attention while you're away.",
      "attention-os-permission",
      {
        label: "Enable",
        run: () => {
          void Notification.requestPermission();
        },
      },
    );
  }
  // Denied: the browser remembers; sounds and the title badge still carry the event.
}

function show(event: AttentionEvent): void {
  const notification = new Notification(event.label, {
    body: BODY[event.kind],
    // Per-session tag coalesces repeat pings; renotify re-alerts on each. Silent: the pack player owns audio.
    tag: `${event.backendId}:${event.slot}`,
    silent: true,
    renotify: true,
  } as NotificationOptions);
  notification.onclick = () => {
    window.focus();
    void dispatchCommand(CommandIds.focusSession, { id: event.slot, backendId: event.backendId });
    notification.close();
  };
}

// ——— Tab-title badge: ● while any session wants attention and the window is unfocused ———

const baseTitle = document.title;

/** Marks the tab title with ● (set on an unfocused attention event; cleared when the window regains focus). */
export function setTitleBadge(on: boolean): void {
  document.title = on ? `● ${baseTitle}` : baseTitle;
}

window.addEventListener("focus", () => setTitleBadge(false));
