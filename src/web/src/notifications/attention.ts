// The session-attention intake: applies the per-event gates and the delivery matrix
// (docs/specs/session-attention.md) — the watched session never pings, a background session pings with
// sound, an unfocused window escalates to an OS notification + title badge. Module-load side effect
// (like session-store), imported once from App.

import { type AttentionKindName, registerSessionFeature, selectedSession } from "../bridge";
import { windowFocused } from "../chrome/window-state";
import { notificationPrefs } from "./prefs";
import { presentOsNotification, setTitleBadge } from "./presenter";
import { playAttentionSound } from "./sounds";

registerSessionFeature((session) =>
  session
    .feature("attention")
    .on<{ label: string; kind: AttentionKindName }>("raised", (message) => {
      const prefs = notificationPrefs();
      if (!prefs.gates[message.kind]) {
        return;
      }
      const focused = windowFocused();
      if (focused && selectedSession() === session) {
        return;
      }
      if (prefs.sounds) {
        void playAttentionSound(message.kind);
      }
      if (!focused) {
        setTitleBadge(true);
        if (prefs.os) {
          presentOsNotification({
            backendId: session.connection.id,
            slot: session.address.slot,
            label: message.label,
            kind: message.kind,
          });
        }
      }
    }),
);
