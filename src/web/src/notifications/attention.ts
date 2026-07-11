// The session-attention intake: the single consumer of `session-attention` pushes from every connected
// backend. Applies the per-event setting gates and the delivery matrix (docs/specs/session-attention.md):
// the session you're actively looking at never pings; a background session pings with sound; an unfocused
// window escalates to an OS notification + title badge. Module-load side effect (like session-store) —
// imported once from App.

import { type AttentionKindName, type NotificationPrefs, onSessionMessage } from "../bridge";
import { sessions } from "../chrome/session-store";
import { notificationPrefs } from "./prefs";
import { presentOsNotification, setTitleBadge } from "./presenter";
import { playAttentionSound } from "./sounds";

function eventEnabled(prefs: NotificationPrefs, kind: AttentionKindName): boolean {
  switch (kind) {
    case "turnComplete":
      return prefs.onTurnComplete;
    case "needsInput":
      return prefs.onNeedsInput;
    case "failed":
      return prefs.onFailed;
  }
}

onSessionMessage((message, backendId) => {
  if (message.type !== "session-attention") {
    return;
  }
  const prefs = notificationPrefs();
  if (!eventEnabled(prefs, message.kind)) {
    return;
  }
  const focused = document.hasFocus();
  // RailSession.active is already gated on its backend driving the page, so one predicate covers both
  // "this backend is active" and "this chip is the active one".
  const watching =
    focused &&
    sessions().some((s) => s.backendId === backendId && s.id === message.slot && s.active);
  if (watching) {
    return;
  }
  if (prefs.sounds) {
    void playAttentionSound(message.kind);
  }
  if (!focused) {
    setTitleBadge(true);
    if (prefs.os) {
      presentOsNotification({
        backendId,
        slot: message.slot,
        label: message.label,
        kind: message.kind,
      });
    }
  }
});
