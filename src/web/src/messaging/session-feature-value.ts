import { createSignal } from "solid-js";
import { type ClientSession, registerSessionFeature } from "../bridge";

/** Owns one pushed feature value per live session, including automatic cleanup when that session closes. */
export function createSessionFeatureValue<TMessage, TValue>(
  featureName: string,
  eventName: string,
  select: (message: TMessage) => TValue,
): (session: ClientSession | null) => TValue | null {
  const [values, setValues] = createSignal(new Map<ClientSession, TValue>());
  registerSessionFeature((session) => {
    const off = session.feature(featureName).on<TMessage>(eventName, (message) => {
      setValues((previous) => new Map(previous).set(session, select(message)));
    });
    return () => {
      off();
      setValues((previous) => {
        const next = new Map(previous);
        next.delete(session);
        return next;
      });
    };
  });
  return (session) => (session === null || session.closed ? null : (values().get(session) ?? null));
}
