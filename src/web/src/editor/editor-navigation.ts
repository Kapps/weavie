import type { ClientSession } from "../bridge";
import { createNavHistory, type NavHistory, type NavLocation } from "./nav-history";

/** History and in-flight restoration belong to one exact session, including when its view detaches. */
export function createEditorNavigation(options: {
  capture(session: ClientSession): NavLocation | undefined;
  restore(session: ClientSession, location: NavLocation, signal: AbortSignal): Promise<void>;
  changed(): void;
  failed(session: ClientSession, error: unknown): void;
}) {
  const timers = new Map<ClientSession, ReturnType<typeof setTimeout>>();
  const cancelRecord = (session: ClientSession): void => {
    clearTimeout(timers.get(session));
    timers.delete(session);
  };
  const operations = new Map<ClientSession, AbortController>();
  const operationFor = (session: ClientSession): AbortController => {
    let operation = operations.get(session);
    if (operation === undefined) {
      operation = new AbortController();
      operations.set(session, operation);
    }
    return operation;
  };
  const histories = new WeakMap<ClientSession, NavHistory>();
  const pending = new Map<ClientSession, { lifetime: AbortController; promise: Promise<void> }>();
  const history = (session: ClientSession): NavHistory => {
    let value = histories.get(session);
    if (value === undefined) {
      value = createNavHistory(async (location) => {
        pending.get(session)?.lifetime.abort();
        operationFor(session).abort();
        const lifetime = new AbortController();
        operations.set(session, lifetime);
        const signal = AbortSignal.any([session.signal, lifetime.signal]);
        const promise = options.restore(session, location, signal);
        const operation = { lifetime, promise };
        pending.set(session, operation);
        try {
          await promise;
        } catch (error) {
          if (!signal.aborted) options.failed(session, error);
          throw error;
        } finally {
          if (pending.get(session) === operation) pending.delete(session);
          options.changed();
        }
      }, options.changed);
      histories.set(session, value);
    }
    return value;
  };
  const commit = (
    session: ClientSession,
    location: NavLocation,
    method: "record" | "push",
  ): void => {
    cancelRecord(session);
    history(session)[method](location);
    options.changed();
  };
  const record = (session: ClientSession, location: NavLocation): void =>
    commit(session, location, "record");
  return {
    history,
    record,
    push: (session: ClientSession, location: NavLocation): void =>
      commit(session, location, "push"),
    signal: (session: ClientSession): AbortSignal =>
      AbortSignal.any([session.signal, operationFor(session).signal]),
    schedule(session: ClientSession, location: NavLocation, signal: AbortSignal): void {
      cancelRecord(session);
      if (!history(session).canRecord()) return;
      timers.set(
        session,
        setTimeout(() => {
          timers.delete(session);
          if (!signal.aborted && !session.signal.aborted) record(session, location);
        }, 150),
      );
    },
    depart(session: ClientSession): void {
      if (pending.has(session)) {
        history(session).cancel();
        pending.delete(session);
      }
      operationFor(session).abort();
      operations.set(session, new AbortController());
      this.capture(session);
    },
    capture(session: ClientSession): void {
      cancelRecord(session);
      const location = options.capture(session);
      if (location !== undefined) record(session, location);
    },
    detach(session: ClientSession): void {
      cancelRecord(session);
      if (pending.has(session)) {
        history(session).cancel();
        pending.delete(session);
      }
      operations.get(session)?.abort();
      operations.delete(session);
    },
    dispose(): void {
      for (const operation of pending.values()) operation.lifetime.abort();
      pending.clear();
      for (const operation of operations.values()) operation.abort();
      operations.clear();
      for (const session of timers.keys()) cancelRecord(session);
    },
  };
}
