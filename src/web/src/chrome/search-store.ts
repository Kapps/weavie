import { createMemo, createSignal } from "solid-js";
import { onHostMessage, postToHost, type SearchMatch } from "../bridge";
import {
  groupByFile,
  moveSelection,
  type SearchOptions,
  visibleIndices as visibleOf,
} from "./search-model";
import { commitSearchTerm, recentTerms, searchOptions, updateSearchOptions } from "./search-prefs";

// Module-level store: the query and results survive close/reopen (Esc doesn't cost a tuned search), and F4
// result-stepping works from the editor without the panel mounted. The panel is a thin view over this; the
// persisted options + recent-terms history live in search-prefs (host-backed).

// Debounce so each keystroke doesn't spawn a git grep; ~200ms is responsive without thrashing.
const DEBOUNCE_MS = 200;
// Settle time before an arrow-selected row live-previews, so holding the key doesn't open every file it passes.
const PREVIEW_MS = 120;

const [query, setQueryRaw] = createSignal("");
const options = searchOptions;
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
// The session whose worktree the current results came from; a switch invalidates them (paths route elsewhere).
let resultsSession: string | null = null;
// How results open in the editor — injected by App (the editor controller), like setNotifySink.
let opener: (match: SearchMatch, focus: boolean) => void = () => {};

function clearResults(): void {
  setMatches([]);
  setTruncated(false);
  setError(null);
  setSelected(-1);
  setCollapsed(new Set<string>());
}

onHostMessage((message) => {
  if (message.type === "find-in-files-results" && message.token === token) {
    setMatches(message.matches);
    setTruncated(message.truncated);
    setError(message.error ?? null);
    setSettled(true);
    setSelected(message.matches.length > 0 ? 0 : -1);
    setCollapsed(new Set<string>());
    setApplied(sentFor);
  } else if (message.type === "set-editor-session" && message.sessionId !== resultsSession) {
    // A session switch: the results (and any pending reply) belong to the previous worktree, so drop them —
    // else F4 (ungated, works with the panel closed) would open a stale path into the new session. The query
    // and options are kept; the user re-runs against the new worktree.
    resultsSession = message.sessionId ?? null;
    token += 1; // orphan any in-flight reply for the old session
    clearResults();
    setSettled(true);
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

/** Sets the query from typing; the search runs debounced. Typing exits history cycling. */
export function setQuery(value: string): void {
  historyCursor = -1;
  setQueryRaw(value);
  scheduleSearch();
}

/** Sets an include/exclude glob list from typing; persists it and re-runs the search debounced. */
export function setGlobs(key: "include" | "exclude", value: string): void {
  updateSearchOptions({ ...options(), [key]: value });
  scheduleSearch();
}

/** Flips a match option (persisted) and re-searches immediately (a click/chord, not typing). */
export function toggleSearchOption(
  key: "caseSensitive" | "wholeWord" | "regex" | "excludeGitignored",
): boolean {
  updateSearchOptions({ ...options(), [key]: !options()[key] });
  runSearch();
  return true;
}

// The history cursor: -1 = showing the live typed query; 0..n index into recentTerms() (most-recent-first).
// `liveQuery` remembers what the user had typed before they started cycling, so Alt+Down past the newest
// restores it.
let historyCursor = -1;
let liveQuery = "";

/** Alt+Up/Down: cycle recent terms (dir +1 = older, -1 = newer). False when there's no history. */
export function cycleHistory(dir: number): boolean {
  const terms = recentTerms();
  if (terms.length === 0) {
    return false;
  }
  if (historyCursor === -1) {
    if (dir < 0) {
      return true; // already at the live query — nothing newer to show
    }
    liveQuery = query();
  }
  historyCursor = Math.min(Math.max(historyCursor + dir, -1), terms.length - 1);
  setQueryRaw(historyCursor === -1 ? liveQuery : (terms[historyCursor] ?? ""));
  runSearch();
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

/** Enter/click commit: open the selected row, record the term in history, hand focus to the editor. */
export function openSelected(): void {
  window.clearTimeout(previewTimer);
  if (visible().includes(selected())) {
    commitSearchTerm(applied().query);
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
  commitSearchTerm(applied().query);
  openIndex(next, true);
  return true;
}

/** Records the current search in history (called when the panel closes, so a run-but-unopened search is kept). */
export function commitCurrentTerm(): void {
  commitSearchTerm(applied().query);
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
