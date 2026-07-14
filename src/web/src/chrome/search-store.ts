import { createMemo, createSignal } from "solid-js";
import { onHostMessage, postToHost, type SearchMatch } from "../bridge";
import {
  groupByFile,
  moveSelection,
  type SearchOptions,
  visibleIndices as visibleOf,
} from "./search-model";

// Module-level store: the query, options, and results survive close/reopen (Esc doesn't cost a tuned search),
// and F4 result-stepping works from the editor without the panel mounted. The panel is a thin view over this.

// Debounce so each keystroke doesn't spawn a git grep; ~200ms is responsive without thrashing.
const DEBOUNCE_MS = 200;
// Settle time before an arrow-selected row live-previews, so holding the key doesn't open every file it passes.
const PREVIEW_MS = 120;

const [query, setQueryRaw] = createSignal("");
const [options, setOptions] = createSignal<SearchOptions>({
  caseSensitive: false,
  wholeWord: false,
  regex: false,
  include: "",
  exclude: "",
});
const [filtersOpen, setFiltersOpen] = createSignal(false);
const [matches, setMatches] = createSignal<SearchMatch[]>([]);
const [truncated, setTruncated] = createSignal(false);
// The git-search error (e.g. a bad regex), so a failed search isn't reported as "No results".
const [error, setError] = createSignal<string | null>(null);
// Whether a reply for the latest request has arrived, so "No results" only shows once the search settled.
const [settled, setSettled] = createSignal(true);
// The selected row in the flattened match list (index into matches()); -1 when there are none.
const [selected, setSelected] = createSignal(-1);
const [collapsed, setCollapsed] = createSignal<ReadonlySet<string>>(new Set<string>());
// What produced the current matches — previews highlight against this, never a newer half-typed query.
const [applied, setApplied] = createSignal<{ query: string; options: SearchOptions }>({
  query: "",
  options: options(),
});
// Bumped by every seed (panel open / re-open), so a mounted panel refocuses + reselects its input.
const [seedNonce, setSeedNonce] = createSignal(0);

const groups = createMemo(() => groupByFile(matches()));
const visible = createMemo(() => visibleOf(matches(), collapsed()));

// Monotonic request token: a reply is applied only when it echoes the latest, so a stale grep never lands.
let token = 0;
let sentFor = applied();
let debounceTimer = 0;
let previewTimer = 0;
// How results open in the editor — injected by App (the editor controller), like setNotifySink.
let opener: (match: SearchMatch, focus: boolean) => void = () => {};

onHostMessage((message) => {
  if (message.type === "find-in-files-results" && message.token === token) {
    setMatches(message.matches);
    setTruncated(message.truncated);
    setError(message.error ?? null);
    setSettled(true);
    setSelected(message.matches.length > 0 ? 0 : -1);
    setCollapsed(new Set<string>());
    setApplied(sentFor);
  }
});

/** Injects how a result opens in the editor (App wires the editor controller's openMatch). */
export function setSearchOpener(fn: (match: SearchMatch, focus: boolean) => void): void {
  opener = fn;
}

function runSearch(): void {
  window.clearTimeout(debounceTimer);
  token += 1;
  const q = query();
  if (q.length === 0) {
    setMatches([]);
    setTruncated(false);
    setError(null);
    setSettled(true);
    setSelected(-1);
    setCollapsed(new Set<string>());
    setApplied({ query: "", options: options() });
    return;
  }
  sentFor = { query: q, options: options() };
  setSettled(false);
  postToHost({ type: "find-in-files", token, query: q, ...options() });
}

function scheduleSearch(): void {
  window.clearTimeout(debounceTimer);
  debounceTimer = window.setTimeout(runSearch, DEBOUNCE_MS);
}

/** Sets the query from typing; the search runs debounced. */
export function setQuery(value: string): void {
  setQueryRaw(value);
  scheduleSearch();
}

/** Sets an include/exclude glob list from typing; the search re-runs debounced. */
export function setGlobs(key: "include" | "exclude", value: string): void {
  setOptions((o) => ({ ...o, [key]: value }));
  scheduleSearch();
}

/** Flips a match option and re-searches immediately (a click/chord, not typing). */
export function toggleSearchOption(key: "caseSensitive" | "wholeWord" | "regex"): boolean {
  setOptions((o) => ({ ...o, [key]: !o[key] }));
  runSearch();
  return true;
}

/** Shows/hides the include-exclude filter row. */
export function toggleSearchFilters(): boolean {
  setFiltersOpen((v) => !v);
  return true;
}

/**
 * Seeds the panel from the editor selection (single-line text replaces the query and searches immediately;
 * null keeps the prior query) and bumps the seed nonce so the panel focuses + selects its input.
 */
export function seedSearch(text: string | null): void {
  if (text !== null && text.length > 0) {
    setQueryRaw(text);
    runSearch();
  }
  setSeedNonce((n) => n + 1);
}

function openIndex(index: number, focus: boolean): void {
  const match = matches()[index];
  if (match !== undefined) {
    opener(match, focus);
  }
}

/** Selects a row (a click) — no preview; the click's own open follows. */
export function selectMatch(index: number): void {
  setSelected(index);
}

/** Arrow navigation: move the selection and live-preview the row (debounced, without stealing focus). */
export function moveAndPreview(delta: number): void {
  const vis = visible();
  if (vis.length === 0) {
    return;
  }
  const next = moveSelection(vis, selected(), delta);
  setSelected(next);
  window.clearTimeout(previewTimer);
  previewTimer = window.setTimeout(() => openIndex(next, false), PREVIEW_MS);
}

/** Enter/click commit: open the selected row and hand focus to the editor. Declines when hidden/none. */
export function openSelected(): void {
  window.clearTimeout(previewTimer);
  if (visible().includes(selected())) {
    openIndex(selected(), true);
  }
}

/** F4 stepping: jump to the next/previous result and open it focused. False when there are no results. */
export function stepSearchResult(delta: number): boolean {
  const vis = visible();
  if (vis.length === 0) {
    return false;
  }
  const next = moveSelection(vis, selected(), delta);
  setSelected(next);
  window.clearTimeout(previewTimer);
  openIndex(next, true);
  return true;
}

/** Drops a pending live preview (the panel is closing). */
export function cancelPreview(): void {
  window.clearTimeout(previewTimer);
}

/** Collapses/expands a file group. */
export function toggleGroup(path: string): void {
  setCollapsed((current) => {
    const next = new Set(current);
    if (next.has(path)) {
      next.delete(path);
    } else {
      next.add(path);
    }
    return next;
  });
}

export const searchState = {
  query,
  options,
  filtersOpen,
  matches,
  truncated,
  error,
  settled,
  selected,
  collapsed,
  applied,
  seedNonce,
  groups,
  visible,
};
