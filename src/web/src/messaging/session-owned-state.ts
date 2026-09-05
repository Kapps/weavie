import { createSignal } from "solid-js";
import { type ClientSession, registerSessionFeature } from "../bridge";

export interface SessionOwnedState<T> {
  get(session: ClientSession | null): T | undefined;
  update(session: ClientSession, apply: (state: T) => T): void;
}

export function createSessionOwnedState<T>(
  create: (session: ClientSession) => T,
): SessionOwnedState<T> {
  return createStore(create, () => {}, false);
}

export function createSessionOwnedResource<T>(
  create: (session: ClientSession) => T,
  release: (session: ClientSession, state: T) => void,
): SessionOwnedState<T> {
  return createStore(create, release, true);
}

export function createSessionOwnedMap<TKey, TValue>() {
  const values = createSessionOwnedState<Map<TKey, TValue>>(() => new Map());
  return {
    get: (session: ClientSession | null, key: TKey): TValue | undefined =>
      values.get(session)?.get(key),
    set: (session: ClientSession, key: TKey, value: TValue): void =>
      void values.get(session)?.set(key, value),
    delete: (session: ClientSession, key: TKey): void => void values.get(session)?.delete(key),
    clear: (session: ClientSession): void => values.get(session)?.clear(),
  };
}

function createStore<T>(
  create: (session: ClientSession) => T,
  release: (session: ClientSession, state: T) => void,
  eager: boolean,
): SessionOwnedState<T> {
  const [states, setStates] = createSignal(new Map<ClientSession, T>());

  const ensure = (session: ClientSession | null): T | undefined => {
    if (session === null || session.closed) {
      return undefined;
    }
    const current = states();
    if (current.has(session)) {
      return current.get(session) as T;
    }
    const state = create(session);
    setStates(new Map(current).set(session, state));
    return state;
  };

  const clear = (session: ClientSession): void => {
    const next = new Map(states());
    if (!next.has(session)) {
      return;
    }
    const state = next.get(session) as T;
    next.delete(session);
    setStates(next);
    release(session, state);
  };

  registerSessionFeature((session) => {
    if (eager) ensure(session);
    return () => clear(session);
  });

  return {
    get: ensure,
    update: (session, apply) => {
      const state = ensure(session);
      if (state === undefined) {
        return;
      }
      setStates(new Map(states()).set(session, apply(state)));
    },
  };
}
