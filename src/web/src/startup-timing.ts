// Startup phase marks for the web app, gated on the ?startuptiming query flag the host adds when the
// `diagnostics.startupTiming` setting is on (off by default — a real setting, never a buried flag).
// Each mark logs ms-since-navigation to the host console so a launch can be timed end to end: the host
// reports window → navigate, and these report navigate → shell-mounted → editor-ready.

import { log } from "./bridge";

const ENABLED = new URLSearchParams(location.search).has("startuptiming");

/** Logs a startup phase mark (ms since page navigation) to the host console when timing is enabled. */
export function mark(phase: string): void {
  if (ENABLED) {
    log("info", `[startup/web] ${phase} +${performance.now().toFixed(0)}ms`);
  }
}
