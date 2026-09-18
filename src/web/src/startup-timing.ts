import { hostConnection, LOCAL_BACKEND_ID, log } from "./bridge";

const pending: string[] = [];
const connection = hostConnection(LOCAL_BACKEND_ID);
connection?.onHello(() => {
  for (const message of pending.splice(0)) log("info", message);
});

/** Captures navigation-relative timings, retaining early marks until the host connects. */
export function mark(phase: string): void {
  const message = `[startup/web] ${phase} +${performance.now().toFixed(0)}ms since navigation`;
  if (connection?.currentHello) log("info", message);
  else pending.push(message);
}
