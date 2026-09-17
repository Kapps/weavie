import { hostConnection, LOCAL_BACKEND_ID, log } from "./bridge";

const pending: string[] = [];
const connection = hostConnection(LOCAL_BACKEND_ID);

function flush(): void {
  if (connection?.currentHello == null) {
    return;
  }
  for (const message of pending.splice(0)) {
    log("info", message);
  }
}

connection?.onHello(flush);

/** Captures navigation-relative timings immediately and delivers them to View Logs once connected. */
export function mark(phase: string): void {
  pending.push(`[startup/web] ${phase} +${performance.now().toFixed(0)}ms since navigation`);
  flush();
}
