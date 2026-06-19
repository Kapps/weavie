import { createSignal } from "solid-js";
import { onHostMessage, postToHost } from "../bridge";
import type { EditorSession, EditorSessionEntry, EditorViewState } from "./session-types";

// The live editor session — the canonical, ordered set of open tabs (active + per-file view state). Seeded by
// the host's set-editor-session push at page load, then mutated in place by the helpers below. The listener is
// registered at module load — which runs before main.tsx sends "ready" — so the host's reply can never race
// ahead of it. Crucially this is a TOP-LEVEL module signal that is NOT reloaded when the App or the editor
// chunk hot-swaps, so it survives HMR: the editor host re-reads it on re-create and restores the exact live
// state (the same trick that keeps layout/store.ts alive across HMR). It MUST therefore be imported at top
// level (by App.tsx), not only through the dynamically-imported editor chunk, or it would reload with that
// chunk and lose state.
//
// Ownership: this store is the source of truth for the tab set. Structural changes flow store → host (the
// controller calls show()/closeFile() off a mutator's result); incidental Monaco state flows host → store,
// data-only (captureViewState). The host never re-opens in reaction to a store write, so there is no loop.
const [session, setSession] = createSignal<EditorSession | null>(null);

// The pinned-first ordering invariant lives in exactly one place: pinned tabs (in pin order) precede unpinned
// (in open order). A stable partition — every mutator routes its result through here, and the host's seed runs
// it defensively, so no other code reorders tabs.
function normalize(open: EditorSessionEntry[]): EditorSessionEntry[] {
  const pinned = open.filter((entry) => entry.pinned);
  const rest = open.filter((entry) => !entry.pinned);
  return pinned.length === 0 ? open : [...pinned, ...rest];
}

// The open-tab SET (paths + which is active + preview/pinned), as a string key, so we only push
// open-editors-changed when the set actually changes — not on the frequent view-state-only commits.
function structureKey(session: EditorSession): string {
  return JSON.stringify({
    active: session.active,
    open: session.open.map((entry) => [entry.path, entry.preview === true, entry.pinned === true]),
  });
}
let lastStructure = "";

// Tell the host the live open-tab set so Claude's getOpenEditors reports it (and close_tab can target it).
function emitOpenEditors(session: EditorSession): void {
  lastStructure = structureKey(session);
  postToHost({
    type: "open-editors-changed",
    editors: session.open.map((entry) => ({
      path: entry.path,
      isActive: entry.path === session.active,
      isPinned: entry.pinned === true,
      isPreview: entry.preview === true,
    })),
  });
}

onHostMessage((message) => {
  if (message.type === "set-editor-session") {
    const seeded = { active: message.session.active, open: normalize(message.session.open) };
    setSession(seeded);
    // Report the restored set so getOpenEditors works immediately, without waiting for a tab change.
    emitOpenEditors(seeded);
  }
});

/// The most recent editor session (host launch push, or the live local state), or null until one arrives.
export const editorSession = session;

/// The open tabs in display order, or [] until a session arrives.
export function openTabs(): EditorSessionEntry[] {
  return session()?.open ?? [];
}

/// The path of the active tab, or null when nothing is open.
export function activePath(): string | null {
  return session()?.active ?? null;
}

// Where to place the editor when it switches to a tab: reveal a 1-based line (a fresh open / explicit
// navigation), or restore the tab's saved Monaco view state (switching back to an already-open tab).
export type Placement = { line: number } | { viewState: EditorViewState | null };

/// A request for the controller to show `path` placed by `placement` (the result of a store mutation that
/// changed which tab is active).
export interface ActivateResult {
  path: string;
  placement: Placement;
}

/// The result of closing a tab: the path whose working copy the host should release, plus the tab to activate
/// next (null when nothing is left open).
export interface CloseResult {
  disposed: string;
  next: ActivateResult | null;
}

// The host persist is debounced: the cursor/scroll hooks fire rapidly and the host only needs the settled
// state (mirrors the layout store's debounced layout-changed). Sets the live signal (so a hot reload restores
// the exact current tab set) AND posts the debounced editor-session-changed. Never sends file content — disk
// is the source of truth.
let postTimer: ReturnType<typeof setTimeout> | undefined;
function commit(next: EditorSession): void {
  setSession(next);
  if (postTimer !== undefined) {
    clearTimeout(postTimer);
  }
  postTimer = setTimeout(() => {
    postTimer = undefined;
    // Normalize each entry to the on-the-wire shape (path + opaque view state + flags); the host persists this
    // verbatim and reads file contents from disk itself. Flags are omitted when false so old files round-trip.
    const open = next.open.map((entry) => ({
      path: entry.path,
      viewState: entry.viewState ?? null,
      ...(entry.preview ? { preview: true } : {}),
      ...(entry.pinned ? { pinned: true } : {}),
      ...(entry.scratch ? { scratch: true } : {}),
    }));
    postToHost({ type: "editor-session-changed", session: { active: next.active, open } });
  }, 300);

  // Push the open-tab set immediately when it changes (a new/closed/activated/pinned/promoted tab) — but not
  // on a view-state-only commit (cursor/scroll), which leaves the structure key unchanged.
  if (structureKey(next) !== lastStructure) {
    emitOpenEditors(next);
  }
}

/// Opens `path`: activates it if already open, otherwise adds a tab and activates it. A `preview` open reuses
/// the single preview slot (so navigating doesn't pile up tabs); a persistent open of a currently-preview tab
/// promotes it. Returns the file to show + how to place it — an explicit `line` (> 1) wins, else the tab's
/// saved view state.
export function openTab(
  path: string,
  opts: { line?: number; preview?: boolean; scratch?: boolean } = {},
): ActivateResult {
  const current = session() ?? { active: null, open: [] };
  const line = opts.line ?? 1;
  // A scratch (untitled) buffer is always a persistent tab, never a preview slot.
  const scratch = opts.scratch === true;
  const preview = !scratch && opts.preview === true;
  const existing = current.open.find((entry) => entry.path === path);
  if (existing !== undefined) {
    // Already open: activate it. A persistent (non-preview) open promotes a currently-preview tab.
    const open =
      existing.preview && !preview
        ? current.open.map((entry) => (entry.path === path ? { ...entry, preview: false } : entry))
        : current.open;
    commit({ active: path, open });
    const placement: Placement = line > 1 ? { line } : { viewState: existing.viewState ?? null };
    return { path, placement };
  }
  let open: EditorSessionEntry[];
  if (preview) {
    // At most one preview tab: reuse the existing preview slot in place (keep its position), else append.
    const previewIdx = current.open.findIndex((entry) => entry.preview);
    open =
      previewIdx === -1
        ? normalize([...current.open, { path, viewState: null, preview: true }])
        : current.open.map((entry, i) =>
            i === previewIdx ? { path, viewState: null, preview: true } : entry,
          );
  } else {
    open = normalize([
      ...current.open,
      { path, viewState: null, ...(scratch ? { scratch: true } : {}) },
    ]);
  }
  commit({ active: path, open });
  return { path, placement: { line } };
}

/// Activates an already-open tab, restoring its saved view state. Returns null if the tab isn't open.
export function activateTab(path: string): ActivateResult | null {
  const current = session();
  if (current === null) {
    return null;
  }
  const entry = current.open.find((open) => open.path === path);
  if (entry === undefined) {
    return null;
  }
  commit({ active: path, open: current.open });
  return { path, placement: { viewState: entry.viewState ?? null } };
}

// Picks the tab to activate after some tabs close: keep the current active if it survived, else the survivor
// nearest the closed active's original position (right neighbor preferred, then left).
function nearestSurvivor(
  open: EditorSessionEntry[],
  closed: ReadonlySet<string>,
  active: string | null,
): string | null {
  if (active !== null && !closed.has(active)) {
    return active;
  }
  const activeIdx = open.findIndex((entry) => entry.path === active);
  for (let offset = 1; offset < open.length; offset += 1) {
    const right = open[activeIdx + offset];
    if (right !== undefined && !closed.has(right.path)) {
      return right.path;
    }
    const left = open[activeIdx - offset];
    if (left !== undefined && !closed.has(left.path)) {
      return left.path;
    }
  }
  return null;
}

function entryPlacement(open: EditorSessionEntry[], path: string | null): ActivateResult | null {
  if (path === null) {
    return null;
  }
  const entry = open.find((open) => open.path === path);
  return entry === undefined ? null : { path, placement: { viewState: entry.viewState ?? null } };
}

/// Closes a single tab (any state — an explicit close may close a pinned tab). Returns the path to release and
/// the next tab to activate, or null if the tab wasn't open.
export function closeTab(path: string): CloseResult | null {
  const current = session();
  if (current === null || !current.open.some((entry) => entry.path === path)) {
    return null;
  }
  const closed = new Set([path]);
  const open = current.open.filter((entry) => entry.path !== path);
  const nextActive = nearestSurvivor(current.open, closed, current.active);
  commit({ active: nextActive, open });
  return { disposed: path, next: entryPlacement(open, nextActive) };
}

/// Drops a tab that was opened only to host an openDiff review proposal (see the editor controller), restoring
/// `fallback` as the active tab when the review tab was the one active. Store-only: unlike closeTab it triggers
/// no host re-show (the caller has already restored the editor) and releases no working copy — the review used a
/// transient model, and a rejected brand-new file was never created. No-op if `path` isn't open.
export function dropReviewTab(path: string, fallback: string | null): void {
  const current = session();
  if (current === null || !current.open.some((entry) => entry.path === path)) {
    return;
  }
  const open = current.open.filter((entry) => entry.path !== path);
  const active =
    current.active === path
      ? fallback !== null && open.some((entry) => entry.path === fallback)
        ? fallback
        : null
      : current.active;
  commit({ active, open });
}

/// Converts a saved scratch tab into a real file tab in place: swaps its `scratchPath` for the on-disk
/// `savedPath`, drops the scratch flag, keeps its position (and pin), and makes it active. If `savedPath` is
/// already open in another tab, the scratch entry is dropped and that existing tab is activated instead.
/// Returns the file to show, or null if the scratch tab isn't open.
export function convertScratch(scratchPath: string, savedPath: string): ActivateResult | null {
  const current = session();
  if (current === null) {
    return null;
  }
  const idx = current.open.findIndex((entry) => entry.path === scratchPath);
  if (idx === -1) {
    return null;
  }
  const existing = current.open.find(
    (entry) => entry.path === savedPath && entry.path !== scratchPath,
  );
  if (existing !== undefined) {
    const open = current.open.filter((entry) => entry.path !== scratchPath);
    commit({ active: savedPath, open: normalize(open) });
    return { path: savedPath, placement: { viewState: existing.viewState ?? null } };
  }
  const open = current.open.map((entry, i) =>
    i === idx
      ? { path: savedPath, viewState: null, ...(entry.pinned ? { pinned: true } : {}) }
      : entry,
  );
  commit({ active: savedPath, open: normalize(open) });
  return { path: savedPath, placement: { line: 1 } };
}

/// Closes every tab matching `predicate` that is NOT pinned (bulk closes never touch pinned tabs). Returns the
/// released paths + the next tab to activate.
export function closeMany(predicate: (entry: EditorSessionEntry) => boolean): {
  disposed: string[];
  next: ActivateResult | null;
} {
  const current = session();
  if (current === null) {
    return { disposed: [], next: null };
  }
  const closed = new Set(
    current.open.filter((entry) => predicate(entry) && !entry.pinned).map((entry) => entry.path),
  );
  if (closed.size === 0) {
    return { disposed: [], next: null };
  }
  const open = current.open.filter((entry) => !closed.has(entry.path));
  const nextActive = nearestSurvivor(current.open, closed, current.active);
  commit({ active: nextActive, open });
  return { disposed: [...closed], next: entryPlacement(open, nextActive) };
}

/// Pins or unpins a tab. Pinning promotes a preview tab (a pinned tab is never preview) and reorders so pinned
/// tabs stay furthest-left.
export function togglePin(path: string): void {
  const current = session();
  if (current === null) {
    return;
  }
  const open = current.open.map((entry) => {
    if (entry.path !== path) {
      return entry;
    }
    // Pinning promotes a preview tab (a pinned tab is never preview); unpinning leaves preview untouched.
    return entry.pinned ? { ...entry, pinned: false } : { ...entry, pinned: true, preview: false };
  });
  commit({ active: current.active, open: normalize(open) });
}

/// Promotes a preview tab to persistent (no-op if it isn't a preview tab).
export function promote(path: string): void {
  const current = session();
  if (current === null) {
    return;
  }
  let changed = false;
  const open = current.open.map((entry) => {
    if (entry.path === path && entry.preview) {
      changed = true;
      return { ...entry, preview: false };
    }
    return entry;
  });
  if (changed) {
    commit({ active: current.active, open });
  }
}

/// Records a tab's latest Monaco view state (scroll/cursor/folding). Data-only: it never changes which tab is
/// active or their order — the host calls it as the editor's position settles and just before a swap.
export function captureViewState(path: string, viewState: EditorViewState | null): void {
  const current = session();
  if (current === null) {
    return;
  }
  let changed = false;
  const open = current.open.map((entry) => {
    if (entry.path === path) {
      changed = true;
      return { ...entry, viewState };
    }
    return entry;
  });
  if (changed) {
    commit({ active: current.active, open });
  }
}
