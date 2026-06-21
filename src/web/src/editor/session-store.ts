import { createSignal } from "solid-js";
import { onHostMessage, postToHost } from "../bridge";
import { samePath } from "./fs-path";
import type { EditorSession, EditorSessionEntry, EditorViewState } from "./session-types";

// The live editor session — the canonical, ordered set of open tabs (active + per-file view state). Seeded by
// the host's set-editor-session push at page load, then mutated in place by the helpers below. A top-level
// module signal that survives HMR (the editor host re-reads it on re-create), so it must be imported at top
// level (by App.tsx), not only through the dynamic editor chunk, or it would reload with that chunk.
//
// Ownership: this store is the source of truth for the tab set. Structural changes flow store → host;
// incidental Monaco state flows host → store, data-only (captureViewState). The host never re-opens in
// reaction to a store write, so there is no loop.
const [session, setSession] = createSignal<EditorSession | null>(null);

// The pinned-first ordering invariant in one place: pinned tabs (in pin order) precede unpinned (in open
// order). Every mutator routes its result through this stable partition, so no other code reorders tabs.
function normalize(open: EditorSessionEntry[]): EditorSessionEntry[] {
  const pinned = open.filter((entry) => entry.pinned);
  const rest = open.filter((entry) => !entry.pinned);
  return pinned.length === 0 ? open : [...pinned, ...rest];
}

// The open-tab set (paths + active + preview/pinned) as a string key, so open-editors-changed is pushed only
// when the set changes, not on frequent view-state-only commits.
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

// Which session owns the current tab set — the id from the last set-editor-session push. Stamped onto every
// editor-session-changed (captured at commit time) so the host can drop a stale change from a session the
// user has since switched away from, instead of misattributing its tabs to the new one.
let ownerSessionId: string | null = null;

onHostMessage((message) => {
  if (message.type === "set-editor-session") {
    ownerSessionId = message.sessionId ?? null;
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

// Where to place the editor on a tab switch: reveal a 1-based line (fresh open / navigation), or restore the
// tab's saved Monaco view state (switching back to an open tab).
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

// The host persist is debounced: cursor/scroll hooks fire rapidly and the host only needs the settled state.
let postTimer: ReturnType<typeof setTimeout> | undefined;

// Send a session to the host as editor-session-changed. Never sends file content — disk is the source of
// truth. Flags are omitted when false so old files round-trip.
function sendEditorSession(s: EditorSession, owner: string | null): void {
  const open = s.open.map((entry) => ({
    path: entry.path,
    viewState: entry.viewState ?? null,
    ...(entry.preview ? { preview: true } : {}),
    ...(entry.pinned ? { pinned: true } : {}),
    ...(entry.scratch ? { scratch: true } : {}),
  }));
  postToHost({
    type: "editor-session-changed",
    sessionId: owner,
    session: { active: s.active, open },
  });
}

function commit(next: EditorSession): void {
  setSession(next);
  // Capture the owner now, not when the timer fires: if a session switch lands a new ownerSessionId before
  // this debounce fires, this change still describes the session active when the user made it, so it must
  // carry that session's id.
  const owner = ownerSessionId;
  if (postTimer !== undefined) {
    clearTimeout(postTimer);
  }
  postTimer = setTimeout(() => {
    postTimer = undefined;
    sendEditorSession(next, owner);
  }, 300);

  // Push the open-tab set immediately when it changes, but not on a view-state-only commit (cursor/scroll),
  // which leaves the structure key unchanged.
  if (structureKey(next) !== lastStructure) {
    emitOpenEditors(next);
  }
}

/// Sends any pending (debounced) editor-session-changed to the host immediately. Called before a session
/// switch so the outgoing session's latest tab set is recorded host-side before the switch rebinds the editor.
export function flushEditorSession(): void {
  if (postTimer === undefined) {
    return;
  }
  clearTimeout(postTimer);
  postTimer = undefined;
  const current = session();
  if (current !== null) {
    // Runs while the outgoing session still owns the tab set, so the live ownerSessionId records it under the
    // correct session.
    sendEditorSession(current, ownerSessionId);
  }
}

/// Opens `path`: activates it if already open, otherwise adds a tab and activates it. A `preview` open reuses
/// the single preview slot; a persistent open of a currently-preview tab promotes it. Returns the file to show
/// + placement — an explicit `line` (> 1) wins, else the tab's saved view state.
export function openTab(
  path: string,
  opts: { line?: number; preview?: boolean; scratch?: boolean } = {},
): ActivateResult {
  const current = session() ?? { active: null, open: [] };
  const line = opts.line ?? 1;
  // A scratch (untitled) buffer is always a persistent tab, never a preview slot.
  const scratch = opts.scratch === true;
  const preview = !scratch && opts.preview === true;
  const existing = current.open.find((entry) => samePath(entry.path, path));
  if (existing !== undefined) {
    // Already open (matched by normalized identity, so a differently-spelled path reuses the tab): activate
    // it, keeping its original stored path so the editor URI / display stay stable. A persistent (non-preview)
    // open promotes a currently-preview tab.
    const open =
      existing.preview && !preview
        ? current.open.map((entry) => (entry === existing ? { ...entry, preview: false } : entry))
        : current.open;
    commit({ active: existing.path, open });
    const placement: Placement = line > 1 ? { line } : { viewState: existing.viewState ?? null };
    return { path: existing.path, placement };
  }
  let open: EditorSessionEntry[];
  if (preview) {
    // At most one preview tab: reuse the existing preview slot in place, else append.
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
  const entry = current.open.find((open) => samePath(open.path, path));
  if (entry === undefined) {
    return null;
  }
  commit({ active: entry.path, open: current.open });
  return { path: entry.path, placement: { viewState: entry.viewState ?? null } };
}

// Picks the tab to activate after some close: keep the current active if it survived, else the survivor
// nearest the closed active's position (right neighbor preferred, then left).
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
  if (current === null) {
    return null;
  }
  const target = current.open.find((entry) => samePath(entry.path, path));
  if (target === undefined) {
    return null;
  }
  const closed = new Set([target.path]);
  const open = current.open.filter((entry) => entry !== target);
  const nextActive = nearestSurvivor(current.open, closed, current.active);
  commit({ active: nextActive, open });
  return { disposed: target.path, next: entryPlacement(open, nextActive) };
}

/// Drops a tab opened only to host an openDiff review proposal (see the editor controller), restoring
/// `fallback` as active when the review tab was active. Store-only: triggers no host re-show and releases no
/// working copy (the review used a transient model). No-op if `path` isn't open.
export function dropReviewTab(path: string, fallback: string | null): void {
  const current = session();
  if (current === null) {
    return;
  }
  const target = current.open.find((entry) => samePath(entry.path, path));
  if (target === undefined) {
    return;
  }
  const open = current.open.filter((entry) => entry !== target);
  const active =
    current.active === target.path
      ? fallback !== null && open.some((entry) => entry.path === fallback)
        ? fallback
        : null
      : current.active;
  commit({ active, open });
}

/// Converts a saved scratch tab into a real file tab in place: swaps `scratchPath` for `savedPath`, drops the
/// scratch flag, keeps its position and pin, and activates it. If `savedPath` is already open elsewhere, the
/// scratch entry is dropped and that tab is activated instead. Returns the file to show, or null if not open.
export function convertScratch(scratchPath: string, savedPath: string): ActivateResult | null {
  const current = session();
  if (current === null) {
    return null;
  }
  const idx = current.open.findIndex((entry) => entry.path === scratchPath);
  if (idx === -1) {
    return null;
  }
  const existing = current.open.find((entry, i) => i !== idx && samePath(entry.path, savedPath));
  if (existing !== undefined) {
    const open = current.open.filter((entry) => entry.path !== scratchPath);
    commit({ active: existing.path, open: normalize(open) });
    return { path: existing.path, placement: { viewState: existing.viewState ?? null } };
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
    if (!samePath(entry.path, path)) {
      return entry;
    }
    // Pinning promotes a preview tab; unpinning leaves preview untouched.
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
    if (entry.preview && samePath(entry.path, path)) {
      changed = true;
      return { ...entry, preview: false };
    }
    return entry;
  });
  if (changed) {
    commit({ active: current.active, open });
  }
}

/// Records a tab's latest Monaco view state (scroll/cursor/folding). Data-only: never changes the active tab
/// or order. Called as the editor's position settles and just before a swap.
export function captureViewState(path: string, viewState: EditorViewState | null): void {
  const current = session();
  if (current === null) {
    return;
  }
  let changed = false;
  const open = current.open.map((entry) => {
    if (samePath(entry.path, path)) {
      changed = true;
      return { ...entry, viewState };
    }
    return entry;
  });
  if (changed) {
    commit({ active: current.active, open });
  }
}
