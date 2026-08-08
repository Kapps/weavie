import { createMemo } from "solid-js";
import { type ClientSession, selectedSession } from "../bridge";
import { createSessionFeatureValue } from "../messaging/session-feature-value";

// Each session's forge ref-link prefix — the URL a terminal "#N" appends its number to. Selection chooses the
// visible prefix; background updates remain attached to their owner.
const prefixFor = createSessionFeatureValue<{ prefix: string | null }, string | null>(
  "git",
  "refLinkBase",
  ({ prefix }) => prefix,
);

/** The selected session's forge ref-link prefix (reactive), or null when a terminal #N isn't linkable. */
export const refLinkPrefix = createMemo(() => {
  return prefixFor(selectedSession());
});

export function refLinkPrefixFor(session: ClientSession): string | null {
  return prefixFor(session);
}
