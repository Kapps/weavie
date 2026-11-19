// The notifications.* settings: the `notifications` device setting. Presentation happens on this device, so
// only its home host supplies them.

import type { NotificationPrefs } from "../bridge";
import { deviceSetting } from "../device-settings";

export type { NotificationPrefs };

declare global {
  interface Window {
    /** Resolved notification prefs injected by the host before navigation; absent in plain-browser dev. */
    __WEAVIE_NOTIFICATIONS__?: NotificationPrefs;
  }
}

// Plain-browser dev fallback (no host injection); in the shipped app a missing value throws (see
// hostInjected). Mirrors the host's defaults: everything on, volume 70, the bundled pack.
const DEFAULT_PREFS: NotificationPrefs = {
  sounds: true,
  os: true,
  volume: 70,
  soundPack: "weavie",
  gates: { turnComplete: true, needsInput: true, failed: true },
};

const prefs = deviceSetting(
  "notifications",
  "__WEAVIE_NOTIFICATIONS__",
  window.__WEAVIE_NOTIFICATIONS__,
  DEFAULT_PREFS,
);

/** The notification prefs to apply right now — read at each attention event, so changes apply live. */
export const notificationPrefs = prefs.current;
