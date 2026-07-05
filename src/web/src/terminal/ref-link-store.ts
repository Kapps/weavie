import { createSignal } from "solid-js";
import { onHostMessage } from "../bridge";

// The active session's forge ref-link prefix — the URL a terminal "#N" appends its number to (e.g.
// https://github.com/owner/repo/pull/), or null when origin isn't a forge repo (so #N stays plain text). Fed by
// the host's active-backend-gated `ref-link-base` push, so it always reflects the visible session's repo.
const [prefix, setPrefix] = createSignal<string | null>(null);

/** The active session's forge ref-link prefix (reactive), or null when a terminal #N isn't linkable. */
export const refLinkPrefix = prefix;

onHostMessage((message) => {
  if (message.type === "ref-link-base") {
    setPrefix(message.prefix);
  }
});
