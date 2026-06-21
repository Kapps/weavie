// Correlated request/reply over the host bridge: a reply may arrive on any message, so each request gets a
// unique id, a pending-resolver table, and a timeout that settles if the host never answers. The caller owns
// the transport (how the request is sent and how a reply is routed back to `settle`) and the timeout policy
// (`onTimeout` resolves by returning a value, or rejects by throwing) — this just owns the id/table/timer.
export interface PendingRequests<T> {
  // Register a pending request. Returns its correlation id (to send with the request) and a promise that
  // resolves when `settle(id, ...)` is called, or settles via `onTimeout` after `timeoutMs`.
  open(timeoutMs: number, onTimeout: () => T): { id: string; promise: Promise<T> };
  // Deliver a reply to the matching pending request (a no-op if it already timed out / unknown id).
  settle(id: string, value: T): void;
}

export function createPendingRequests<T>(prefix: string): PendingRequests<T> {
  let seq = 0;
  const pending = new Map<string, (value: T) => void>();
  return {
    open(timeoutMs, onTimeout) {
      const id = `${prefix}${++seq}`;
      const promise = new Promise<T>((resolve, reject) => {
        const timer = setTimeout(() => {
          pending.delete(id);
          try {
            resolve(onTimeout());
          } catch (error) {
            reject(error);
          }
        }, timeoutMs);
        pending.set(id, (value) => {
          clearTimeout(timer);
          pending.delete(id);
          resolve(value);
        });
      });
      return { id, promise };
    },
    settle(id, value) {
      pending.get(id)?.(value);
    },
  };
}
