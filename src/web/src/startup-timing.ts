import { log } from "./bridge";

/** Logs a navigation-relative startup timing; the bridge holds it until the host connects. */
export function mark(phase: string): void {
  log("info", `[startup/web] ${phase} +${performance.now().toFixed(0)}ms since navigation`);
}
