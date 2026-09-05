import { createSignal } from "solid-js";
import type { ClientSession } from "../bridge";
import { createSessionOwnedResource } from "./session-owned-state";

/** Owns one pushed feature value per live session, including automatic cleanup when that session closes. */
export function createSessionFeatureValue<TMessage, TValue>(
  featureName: string,
  eventName: string,
  select: (message: TMessage) => TValue,
): (session: ClientSession | null) => TValue | null {
  const values = createSessionOwnedResource(
    (session) => {
      const [read, write] = createSignal<TValue | null>(null);
      const stop = session.feature(featureName).on<TMessage>(eventName, (message) => {
        write(() => select(message));
      });
      return [read, stop] as const;
    },
    (_session, [, stop]) => stop(),
  );
  return (session) => values.get(session)?.[0]() ?? null;
}
