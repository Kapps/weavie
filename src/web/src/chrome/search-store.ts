import { createMemo, createSignal } from "solid-js";
import {
  type ClientSession,
  onSelectedSession,
  type SearchMatch,
  selectedSession,
} from "../bridge";
import {
  groupByFile,
  moveSelection,
  type SearchOptions,
  visibleIndices as visibleOf,
} from "./search-model";
import {
  commitSearchTerm,
  recentTerms,
  searchOptions,
  updateSearchOptions,
  updateSearchOptionsDebounced,
} from "./search-prefs";

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
type SearchPhase = "idle" | "searching" | "ready" | "error";
const [phase, setPhase] = createSignal<SearchPhase>("idle");
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

let debounceTimer = 0;
let previewTimer = 0;
interface ActiveSearch {
  controller: AbortController;
  requested: { query: string; options: SearchOptions };
}
const requests = new WeakMap<ClientSession, ActiveSearch>();
const resultsBySession = new WeakMap<ClientSession, SearchResult>();
// How results open in the editor — injected by App (the editor controller), like setNotifySink.
let opener: (match: SearchMatch, focus: boolean) => void = () => {};

function clearResults(): void {
  setMatches([]);
  setTruncated(false);
  setError(null);
  setSelected(-1);
  setCollapsed(new Set<string>());
}

function sameOptions(left: SearchOptions, right: SearchOptions): boolean {
  return (
    left.caseSensitive === right.caseSensitive &&
    left.wholeWord === right.wholeWord &&
    left.regex === right.regex &&
    left.excludeGitignored === right.excludeGitignored &&
    left.include === right.include &&
    left.exclude === right.exclude
  );
}

function matchesDraft(result: SearchResult): boolean {
  return result.applied.query === query() && sameOptions(result.applied.options, options());
}

interface SearchResult {
  matches: SearchMatch[];
  truncated: boolean;
  error: string | null;
  applied: { query: string; options: SearchOptions };
}

function projectResult(result: SearchResult | undefined): void {
  if (result === undefined) {
    clearResults();
    setPhase("idle");
    return;
  }
  setMatches(result.matches);
  setTruncated(result.truncated);
  setError(result.error);
  setPhase(result.error === null ? "ready" : "error");
  setSelected(result.matches.length > 0 ? 0 : -1);
  setCollapsed(new Set<string>());
  setApplied(result.applied);
}

onSelectedSession((session) => {
  const active = session === null ? undefined : requests.get(session);
  if (
    active !== undefined &&
    active.requested.query === query() &&
    sameOptions(active.requested.options, options())
  ) {
    clearResults();
    setPhase("searching");
    return;
  }
  const result = session === null ? undefined : resultsBySession.get(session);
  projectResult(result !== undefined && matchesDraft(result) ? result : undefined);
});

/** Injects how a result opens in the editor (App wires the editor controller's openMatch). */
export function setSearchOpener(fn: (match: SearchMatch, focus: boolean) => void): void {
  opener = fn;
}

function runSearch(): void {
  window.clearTimeout(debounceTimer);
  const q = query();
  if (q.length === 0) {
    setMatches([]);
    setTruncated(false);
    setError(null);
    setPhase("idle");
    setSelected(-1);
    setCollapsed(new Set<string>());
    setApplied({ query: "", options: options() });
    return;
  }
  const requested = { query: q, options: options() };
  const session = selectedSession();
  if (session === null) {
    clearResults();
    setPhase("idle");
    return;
  }
  requests.get(session)?.controller.abort();
  const controller = new AbortController();
  const request = { controller, requested };
  requests.set(session, request);
  void session
    .feature("search")
    .request<
      {
        matches: SearchMatch[];
        truncated: boolean;
        error?: string | null;
      },
      { query: string } & SearchOptions
    >("query", { query: q, ...options() }, controller.signal)
    .then((response) => {
      if (requests.get(session) !== request) {
        return;
      }
      requests.delete(session);
      const result: SearchResult = {
        matches: response.matches,
        truncated: response.truncated,
        error: response.error ?? null,
        applied: requested,
      };
      resultsBySession.set(session, result);
      if (selectedSession() === session && matchesDraft(result)) {
        projectResult(result);
      }
    })
    .catch((error: unknown) => {
      if (controller.signal.aborted || requests.get(session) !== request) {
        return;
      }
      requests.delete(session);
      const result: SearchResult = {
        matches: [],
        truncated: false,
        error: error instanceof Error ? error.message : String(error),
        applied: requested,
      };
      resultsBySession.set(session, result);
      if (selectedSession() === session && matchesDraft(result)) {
        projectResult(result);
      }
    });
}

function beginSearchIntent(): boolean {
  window.clearTimeout(previewTimer);
  const session = selectedSession();
  if (session !== null) {
    requests.get(session)?.controller.abort();
    requests.delete(session);
    resultsBySession.delete(session);
  }
  clearResults();
  if (query().length === 0) {
    setApplied({ query: "", options: options() });
    setPhase("idle");
    return false;
  }
  setPhase("searching");
  return true;
}

function scheduleSearch(): void {
  window.clearTimeout(debounceTimer);
  if (beginSearchIntent()) {
    debounceTimer = window.setTimeout(runSearch, DEBOUNCE_MS);
  }
}

/** Sets the query from typing; the search runs debounced. Typing exits history cycling. */
export function setQuery(value: string): void {
  historyCursor = -1;
  setQueryRaw(value);
  scheduleSearch();
}

/** Sets an include/exclude glob list from typing; persists it and re-runs the search debounced. */
export function setGlobs(key: "include" | "exclude", value: string): void {
  updateSearchOptionsDebounced({ ...options(), [key]: value });
  scheduleSearch();
}

/** Flips a match option (persisted) and re-searches immediately (a click/chord, not typing). */
export function toggleSearchOption(
  key: "caseSensitive" | "wholeWord" | "regex" | "excludeGitignored",
): boolean {
  updateSearchOptions({ ...options(), [key]: !options()[key] });
  if (beginSearchIntent()) {
    runSearch();
  }
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
  if (beginSearchIntent()) {
    runSearch();
  }
  return true;
}

/**
 * Seeds the panel from the editor selection (single-line text replaces the query and searches immediately;
 * null keeps the prior query) and bumps the seed nonce so the panel focuses + selects its input.
 */
export function seedSearch(text: string | null): void {
  if (text !== null && text.length > 0) {
    historyCursor = -1; // a fresh query replaces whatever history entry was being cycled
    setQueryRaw(text);
    if (beginSearchIntent()) {
      runSearch();
    }
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
  if (phase() === "ready") {
    setSelected(index);
  }
}

/** Arrow navigation: move the selection and live-preview the row (debounced, without stealing focus). */
export function moveAndPreview(delta: number): void {
  if (phase() !== "ready") {
    return;
  }
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
  if (phase() !== "ready") {
    return;
  }
  if (visible().includes(selected())) {
    commitSearchTerm(applied().query);
    openIndex(selected(), true);
  }
}

/** F4 stepping: jump to the next/previous result and open it focused. False when there are no results. */
export function stepSearchResult(delta: number): boolean {
  if (phase() !== "ready") {
    return false;
  }
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
  phase,
  selected,
  collapsed,
  applied,
  seedNonce,
  groups,
  visible,
};
