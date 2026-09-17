import { hostConnection, LOCAL_BACKEND_ID, log } from "./bridge";

const enabled = window.__WEAVIE_STARTUP_TIMING__ === true;
const pending: string[] = [];
const connection = enabled ? hostConnection(LOCAL_BACKEND_ID) : undefined;

function flush(): void {
  if (connection?.currentHello == null) {
    return;
  }
  for (const message of pending.splice(0)) {
    log("info", message);
  }
}

if (enabled) {
  connection?.onHello(flush);
}

/** Captures navigation-relative timings immediately and delivers them to View Logs once connected. */
export function mark(phase: string): void {
  if (enabled) {
    pending.push(`[startup/web] ${phase} +${performance.now().toFixed(0)}ms since navigation`);
    flush();
  }
}
