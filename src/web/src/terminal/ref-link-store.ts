import type { ClientSession } from "../bridge";
import { createSessionFeatureValue } from "../messaging/session-feature-value";

// Each session's forge ref-link prefix — the URL a terminal "#N" appends its number to.
const prefixFor = createSessionFeatureValue<{ prefix: string | null }, string | null>(
  "git",
  "refLinkBase",
  ({ prefix }) => prefix,
);

export function refLinkPrefixFor(session: ClientSession): string | null {
  return prefixFor(session);
}
