import { createSignal } from "solid-js";
import { onSessionMessage, postToLocalHost } from "../bridge";
import type { SearchOptions } from "./search-model";

// The find-in-files panel's persisted UI state (match options, include/exclude globs, recent search terms),
// stored host-side in ~/.weavie/search-state.json — never localStorage, and never the search term itself.
// Setters update the local signal optimistically and tell the LOCAL host, which echoes the canonical state
// back. Registered at module load, before main.tsx sends `ready`, so the seed push has a listener. Mirrors
// rail-state.ts.

const DEFAULT_OPTIONS: SearchOptions = {
  caseSensitive: false,
  wholeWord: false,
  regex: false,
  excludeGitignored: true,
  include: "",
  exclude: "",
};

const [optionsSig, setOptionsSig] = createSignal<SearchOptions>(DEFAULT_OPTIONS);
const [recentTermsSig, setRecentTermsSig] = createSignal<string[]>([]);

// Debounce so typing in the include/exclude glob boxes doesn't do a disk write + host round-trip per
// keystroke (mirrors search-store's search debounce, for the same reason).
const PERSIST_DEBOUNCE_MS = 200;
let persistTimer = 0;

// Honored only from the LOCAL backend — a remote runner would push its own machine's file, which must not leak in.
onSessionMessage((message, backendId) => {
  if (message.type === "search-state" && backendId === "local") {
    setOptionsSig(message.options);
    setRecentTermsSig(message.recentTerms);
  }
});

/** The persisted match options + globs (reactive), seeded from the host on load. */
export const searchOptions = optionsSig;

/** The recent search terms, most-recent first (reactive), for the Alt+Up/Down history. */
export const recentTerms = recentTermsSig;

/** Replaces the match options / globs: optimistic local update, then persist to the local host. */
export function updateSearchOptions(next: SearchOptions): void {
  setOptionsSig(next);
  window.clearTimeout(persistTimer);
  postToLocalHost({ type: "set-search-options", ...next });
}

/**
 * Same as updateSearchOptions, but the host persist (disk write + IPC echo) is debounced — for typing
 * paths (the glob boxes), where every keystroke would otherwise trigger a synchronous file write.
 */
export function updateSearchOptionsDebounced(next: SearchOptions): void {
  setOptionsSig(next);
  window.clearTimeout(persistTimer);
  persistTimer = window.setTimeout(
    () => postToLocalHost({ type: "set-search-options", ...next }),
    PERSIST_DEBOUNCE_MS,
  );
}

/** Records a run search term in the MRU history (host owns dedup/bound; it echoes the canonical list back). */
export function commitSearchTerm(term: string): void {
  if (term.trim().length === 0) {
    return;
  }
  postToLocalHost({ type: "add-search-term", term });
}
