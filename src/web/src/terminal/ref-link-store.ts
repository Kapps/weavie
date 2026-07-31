import { createMemo, createSignal } from "solid-js";
import { type ClientSession, registerSessionFeature, selectedSession } from "../bridge";

// Each session's forge ref-link prefix — the URL a terminal "#N" appends its number to. Selection chooses the
// visible prefix; background updates remain attached to their owner.
const [prefixes, setPrefixes] = createSignal(new Map<ClientSession, string | null>());

/** The selected session's forge ref-link prefix (reactive), or null when a terminal #N isn't linkable. */
export const refLinkPrefix = createMemo(() => {
  const session = selectedSession();
  return session === null ? null : (prefixes().get(session) ?? null);
});

export function refLinkPrefixFor(session: ClientSession): string | null {
  return prefixes().get(session) ?? null;
}

registerSessionFeature((session) => {
  const off = session.feature("git").on<{ prefix: string | null }>("refLinkBase", ({ prefix }) => {
    setPrefixes((previous) => new Map(previous).set(session, prefix));
  });
  return () => {
    off();
    setPrefixes((previous) => {
      const next = new Map(previous);
      next.delete(session);
      return next;
    });
  };
});
