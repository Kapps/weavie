// The session-attention intake: applies the per-event gates and the delivery matrix
// (docs/specs/session-attention.md) — the watched session never pings, a background session pings with
// sound, an unfocused window escalates to an OS notification + title badge. Module-load side effect
// (like session-store), imported once from App.

import { onSessionMessage } from "../bridge";
import { findSession } from "../chrome/session-store";
import { windowFocused } from "../chrome/window-state";
import { notificationPrefs } from "./prefs";
import { presentOsNotification, setTitleBadge } from "./presenter";
import { playAttentionSound } from "./sounds";

onSessionMessage((message, backendId) => {
  if (message.type !== "session-attention") {
    return;
  }
  const prefs = notificationPrefs();
  if (!prefs.gates[message.kind]) {
    return;
  }
  // RailSession.active is already gated on its backend driving the page, so one predicate covers both
  // "this backend is active" and "this chip is the active one".
  const focused = windowFocused();
  if (focused && (findSession(backendId, message.slot)?.active ?? false)) {
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
