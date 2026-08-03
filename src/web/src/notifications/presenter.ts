// OS-facing presentation for session attention: the Web Notification API in browser-hosted Weavie and
// the local shell's native notification channel in desktop Weavie. See docs/specs/session-attention.md.

import {
  type AttentionKindName,
  hostConnection,
  isBrowserHostedShell,
  LOCAL_BACKEND_ID,
  registerHostFeature,
} from "../bridge";
import { dispatchCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import type { MessageFeature } from "../messaging/message-bus";
import type { SessionAddress } from "../messaging/message-envelope";
import { notify } from "../notify/notify";

/** One attention event with the rail identity the notification names and focuses. */
export interface AttentionEvent {
  backendId: string;
  slot: string;
  incarnation: string;
  label: string;
  kind: AttentionKindName;
  body: string;
}

type NativePermission = "unavailable" | "notDetermined" | "granted" | "denied";

// The permission toast fires once per page load and PERSISTS (action toasts never auto-dismiss) — it's
// raised while the user is away, so it must still be there when they come back. A denied prompt is
// remembered by the browser itself; an explicit dismissal holds for the page's lifetime.
let permissionPrompted = false;
let nativePermissionPrompted = false;

/** Raises the OS notification for an attention event through the browser or native shell. */
export function presentOsNotification(event: AttentionEvent): void {
  if (!isBrowserHostedShell()) {
    void presentNativeNotification(event);
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
    body: event.body,
    // Per-session tag coalesces repeat pings; renotify re-alerts on each. Silent: the pack player owns audio.
    tag: `${event.backendId}:${event.slot}`,
    silent: true,
    renotify: true,
  } as NotificationOptions);
  notification.onclick = () => {
    window.focus();
    focusSession(event.backendId, { slot: event.slot, incarnation: event.incarnation });
    notification.close();
  };
}

async function presentNativeNotification(event: AttentionEvent): Promise<void> {
  const feature = hostConnection(LOCAL_BACKEND_ID)?.host.feature("notifications");
  if (feature === undefined) {
    reportNativeFailure(new Error("The local notification channel is not connected."));
    return;
  }

  try {
    const { permission } = await feature.request<{ permission: NativePermission }>(
      "permission",
      {},
    );
    if (permission === "granted") {
      await showNativeNotification(feature, event);
    } else if (permission === "notDetermined" && !nativePermissionPrompted) {
      nativePermissionPrompted = true;
      notify(
        "info",
        "Weavie can notify you when a session needs attention while you're away.",
        "attention-os-permission",
        {
          label: "Enable",
          run: () => {
            void feature
              .request<{ permission: NativePermission }>("requestPermission", {})
              .then(async ({ permission: requested }) => {
                if (requested === "granted") {
                  await showNativeNotification(feature, event);
                }
              })
              .catch(reportNativeFailure);
          },
        },
      );
    } else if (permission === "unavailable") {
      reportNativeFailure(new Error("This desktop session has no native notification service."));
    }
  } catch (error) {
    reportNativeFailure(error);
  }
}

async function showNativeNotification(
  feature: MessageFeature,
  event: AttentionEvent,
): Promise<void> {
  await feature.request<
    { shown: boolean },
    { backendId: string; address: SessionAddress; label: string; kind: AttentionKindName }
  >("show", {
    backendId: event.backendId,
    address: { slot: event.slot, incarnation: event.incarnation },
    label: event.label,
    kind: event.kind,
  });
}

function reportNativeFailure(error: unknown): void {
  const detail = error instanceof Error ? error.message : String(error);
  notify("error", `Couldn't show native notifications: ${detail}`, "attention-os-error");
}

function focusSession(backendId: string, address: SessionAddress): void {
  void dispatchCommand(CommandIds.focusSession, {
    id: address.slot,
    backendId,
    incarnation: address.incarnation,
  });
}

registerHostFeature((connection) => {
  if (!connection.isLocal) {
    return;
  }
  return connection.host
    .feature("notifications")
    .on<{ backendId: string; address: SessionAddress }>("activated", ({ backendId, address }) => {
      focusSession(backendId, address);
    });
});

// ——— Tab-title badge: ● while any session wants attention and the window is unfocused ———

const baseTitle = document.title;

/** Marks the tab title with ● (set on an unfocused attention event; cleared when the window regains focus). */
export function setTitleBadge(on: boolean): void {
  document.title = on ? `● ${baseTitle}` : baseTitle;
}

window.addEventListener("focus", () => setTitleBadge(false));
