import { createSignal } from "solid-js";
import { type ClientSession, registerSessionFeature, selectedSession } from "../bridge";
import { samePath } from "./fs-path";
import type {
  EditorSession,
  EditorSessionEntry,
  EditorViewState,
  ReviewResume,
} from "./session-types";
import { isFileTab, matchesTab, tabKind, tabResourceKey } from "./tab-entry";
import { TabOwner } from "./tab-owner";

const states = new WeakMap<ClientSession, OwnedEditorSession>();

function normalize(open: EditorSessionEntry[]): EditorSessionEntry[] {
  const pinned = open.filter((entry) => entry.pinned);
  return pinned.length === 0 ? open : [...pinned, ...open.filter((entry) => !entry.pinned)];
}

function structureKey(session: EditorSession): string {
  return JSON.stringify({
    active: session.active,
    open: session.open.map((entry) => [
      entry.path,
      tabKind(entry),
      entry.preview === true,
      entry.pinned === true,
    ]),
  });
}

class OwnedEditorSession {
  private readonly feature;
  private readonly readState;
  private readonly writeState;
  private postTimer: ReturnType<typeof setTimeout> | undefined;
  private lastStructure = "";
  private readonly structureListeners = new Set<() => void>();
  // A line explicitly requested for a tab that hasn't captured a real Monaco viewState yet. A tab's persisted
  // viewState is the only durable "where to show this" the host round-trips, so a fresh tab always restores at
  // its default (line 1) until the user scrolls it — including a *second*, redundant restore of the same tab
  // (e.g. session selection settling late after a reload) that lands after an explicit reveal already showed
  // it at a specific line. This closes that gap without round-tripping a synthetic viewState: superseded the
  // moment a real one is captured (see activate), never read once the tab is gone.
  private readonly pendingLines = new Map<string, number>();
  private readonly tabs = new Map<string, TabOwner>();

  tab(path: string): TabOwner | undefined {
    const entry = this.readState()?.open.find((entry) => matchesTab(entry, path));
    return entry === undefined ? undefined : this.tabs.get(tabResourceKey(entry));
  }

  private reconcileTabs(next: EditorSession): void {
    const live = new Set<string>();
    for (const entry of next.open) {
      const key = tabResourceKey(entry);
      live.add(key);
      const tab = this.tabs.get(key);
      if (tab === undefined) this.tabs.set(key, new TabOwner(this.owner, entry));
      else tab.entry = entry;
    }
    for (const [key, tab] of this.tabs) {
      if (!live.has(key)) {
        tab.dispose();
        this.tabs.delete(key);
      }
    }
  }

  constructor(private readonly owner: ClientSession) {
    this.feature = owner.feature("editor");
    [this.readState, this.writeState] = createSignal<EditorSession | null>(null);
  }

  get current(): () => EditorSession | null {
    return this.readState;
  }

  pendingLine(path: string): number | undefined {
    return this.pendingLines.get(path);
  }

  restore(session: EditorSession): void {
    this.cancelPending();
    const next = { ...session, open: normalize(session.open) };
    this.reconcileTabs(next);
    this.writeState(next);
    this.emitOpenEditors(next);
    this.notifyStructure();
  }

  openTab(
    path: string,
    opts: {
      line?: number;
      column?: number;
      focus?: boolean;
      preview?: boolean;
      scratch?: boolean;
      kind?: "file" | "web" | "source" | "plan" | "review";
    },
  ): ActivateResult {
    const current = this.readState() ?? { active: null, open: [] };
    const placement: Placement = {
      line: opts.line ?? 1,
      ...(opts.column === undefined ? {} : { column: opts.column }),
      ...(opts.focus === undefined ? {} : { focus: opts.focus }),
    };
    if (opts.line !== undefined) {
      this.pendingLines.set(path, opts.line);
    }
    const scratch = opts.scratch === true;
    const preview = !scratch && opts.preview === true;
    const existing = current.open.find((entry) => matchesTab(entry, path));
    if (existing !== undefined && tabKind(existing) === (opts.kind ?? "file")) {
      const open =
        existing.preview && !preview
          ? current.open.map((entry) => (entry === existing ? { ...entry, preview: false } : entry))
          : current.open;
      this.commit({ active: existing.path, open });
      // A requested line always reveals — including line 1, a created file's whole diff. Only a positionless
      // open (file tree, recents, Go-to-File) restores where the user last was in the tab.
      return {
        path: existing.path,
        placement: opts.line === undefined ? { viewState: existing.viewState ?? null } : placement,
      };
    }

    const entry: EditorSessionEntry = {
      path,
      viewState: null,
      ...(existing?.pinned ? { pinned: true } : {}),
      ...(scratch ? { scratch: true } : {}),
      ...(preview ? { preview: true } : {}),
      ...(opts.kind === undefined ? {} : { kind: opts.kind }),
    };
    const previewIndex = preview ? current.open.findIndex((entry) => entry.preview) : -1;
    const replaced = existing === undefined ? previewIndex : current.open.indexOf(existing);
    const open =
      replaced === -1
        ? normalize([...current.open, entry])
        : current.open.map((previous, index) => (index === replaced ? entry : previous));
    this.commit({ active: path, open });
    return { path, placement };
  }

  activate(path: string): ActivateResult | null {
    const current = this.readState();
    const entry = current?.open.find((candidate) => matchesTab(candidate, path));
    if (current === null || entry === undefined) {
      return null;
    }
    this.commit({ active: entry.path, open: current.open });
    // A freshly opened tab has no captured viewState yet; fall back to the line an explicit reveal just asked
    // for so a redundant activation (e.g. session selection settling late after a reload) can't regress it to
    // the file's top. A real viewState — captured the moment the user actually leaves the tab — always wins.
    const pendingLine = entry.viewState === null ? this.pendingLines.get(entry.path) : undefined;
    return {
      path: entry.path,
      placement:
        pendingLine === undefined ? { viewState: entry.viewState ?? null } : { line: pendingLine },
    };
  }

  close(path: string): CloseResult | null {
    const current = this.readState();
    const target = current?.open.find((entry) => matchesTab(entry, path));
    if (current === null || target === undefined) {
      return null;
    }
    const closed = new Set([target.path]);
    const open = current.open.filter((entry) => entry !== target);
    const active = nearestSurvivor(current.open, closed, current.active);
    this.commit({ active, open });
    this.pendingLines.delete(target.path);
    return { disposed: target.path, next: entryPlacement(open, active) };
  }

  dropReview(path: string, fallback: string | null): void {
    const current = this.readState();
    const target = current?.open.find((entry) => matchesTab(entry, path));
    if (current === null || target === undefined) {
      return;
    }
    const open = current.open.filter((entry) => entry !== target);
    const active =
      current.active === target.path
        ? fallback !== null && open.some((entry) => entry.path === fallback)
          ? fallback
          : null
        : current.active;
    this.commit({ active, open });
  }

  convertScratch(scratchPath: string, savedPath: string): ActivateResult | null {
    const current = this.readState();
    if (current === null) {
      return null;
    }
    const index = current.open.findIndex((entry) => entry.path === scratchPath);
    if (index === -1) {
      return null;
    }
    const existing = current.open.find(
      (entry, candidate) => candidate !== index && samePath(entry.path, savedPath),
    );
    if (existing !== undefined) {
      const open = normalize(current.open.filter((entry) => entry.path !== scratchPath));
      this.commit({ active: existing.path, open });
      return { path: existing.path, placement: { viewState: existing.viewState ?? null } };
    }
    const open = normalize(
      current.open.map((entry, candidate) =>
        candidate === index
          ? { path: savedPath, viewState: null, ...(entry.pinned ? { pinned: true } : {}) }
          : entry,
      ),
    );
    this.commit({ active: savedPath, open });
    return { path: savedPath, placement: { line: 1 } };
  }

  closeMany(predicate: (entry: EditorSessionEntry) => boolean): {
    disposed: string[];
    next: ActivateResult | null;
  } {
    const current = this.readState();
    if (current === null) {
      return { disposed: [], next: null };
    }
    const closed = new Set(
      current.open
        .filter((entry) => predicate(entry) && entry.pinned !== true)
        .map((entry) => entry.path),
    );
    if (closed.size === 0) {
      return { disposed: [], next: null };
    }
    const open = current.open.filter((entry) => !closed.has(entry.path));
    const active = nearestSurvivor(current.open, closed, current.active);
    this.commit({ active, open });
    for (const path of closed) {
      this.pendingLines.delete(path);
    }
    return { disposed: [...closed], next: entryPlacement(open, active) };
  }

  togglePin(path: string): void {
    const current = this.readState();
    if (current === null) {
      return;
    }
    const open = current.open.map((entry) => {
      if (!matchesTab(entry, path)) {
        return entry;
      }
      return entry.pinned
        ? { ...entry, pinned: false }
        : { ...entry, pinned: true, preview: false };
    });
    this.commit({ active: current.active, open: normalize(open) });
  }

  promote(path: string): void {
    const current = this.readState();
    if (current === null) {
      return;
    }
    let changed = false;
    const open = current.open.map((entry) => {
      if (entry.preview && matchesTab(entry, path)) {
        changed = true;
        return { ...entry, preview: false };
      }
      return entry;
    });
    if (changed) {
      this.commit({ active: current.active, open });
    }
  }

  captureViewState(path: string, viewState: EditorViewState | null): void {
    const current = this.readState();
    if (current === null) {
      return;
    }
    let changed = false;
    const open = current.open.map((entry) => {
      if (matchesTab(entry, path)) {
        changed = true;
        return { ...entry, viewState };
      }
      return entry;
    });
    if (changed) {
      this.commit({ active: current.active, open });
    }
  }

  flush(): void {
    if (this.postTimer === undefined) {
      return;
    }
    this.cancelPending();
    const current = this.readState();
    if (current !== null) {
      this.send(current);
    }
  }

  closeState(): void {
    this.cancelPending();
    this.structureListeners.clear();
    for (const tab of this.tabs.values()) tab.dispose();
    this.tabs.clear();
  }

  subscribeStructure(listener: () => void): () => void {
    this.structureListeners.add(listener);
    listener();
    return () => this.structureListeners.delete(listener);
  }

  captureReview(review: ReviewResume): void {
    const current = this.readState();
    if (current !== null) this.commit({ ...current, review });
  }

  private commit(next: EditorSession): void {
    next = { review: this.readState()?.review ?? null, ...next };
    const structureChanged = structureKey(next) !== this.lastStructure;
    this.reconcileTabs(next);
    this.writeState(next);
    this.cancelPending();
    this.postTimer = setTimeout(() => {
      this.postTimer = undefined;
      this.send(next);
    }, 300);
    if (structureChanged) {
      this.emitOpenEditors(next);
      this.notifyStructure();
    }
  }

  private send(session: EditorSession): void {
    const active =
      session.active !== null && session.open.some((entry) => entry.path === session.active)
        ? session.active
        : null;
    this.publish("sessionChanged", {
      session: {
        active,
        review: session.review,
        open: session.open.map((entry) => ({
          path: entry.path,
          ...(entry.kind == null ? {} : { kind: entry.kind }),
          viewState: entry.viewState ?? null,
          ...(entry.preview ? { preview: true } : {}),
          ...(entry.pinned ? { pinned: true } : {}),
          ...(entry.scratch ? { scratch: true } : {}),
        })),
      },
    });
  }

  private emitOpenEditors(session: EditorSession): void {
    this.lastStructure = structureKey(session);
    this.publish("openEditorsChanged", {
      editors: session.open.filter(isFileTab).map((entry) => ({
        path: entry.path,
        isActive: entry.path === session.active,
        isPinned: entry.pinned === true,
        isPreview: entry.preview === true,
      })),
    });
  }

  private publish(name: string, payload: unknown): void {
    try {
      this.feature.publish(name, payload);
    } catch (error) {
      this.owner.connection.reportError(error);
    }
  }

  private cancelPending(): void {
    if (this.postTimer !== undefined) {
      clearTimeout(this.postTimer);
      this.postTimer = undefined;
    }
  }

  private notifyStructure(): void {
    for (const listener of this.structureListeners) {
      listener();
    }
  }
}

registerSessionFeature((owner) => {
  const state = new OwnedEditorSession(owner);
  states.set(owner, state);
  const off = owner.state.editor.subscribe((session) => {
    if (session !== null) {
      state.restore(session);
    }
  });
  return () => {
    off();
    state.closeState();
    states.delete(owner);
  };
});

function selectedState(): OwnedEditorSession | undefined {
  const owner = selectedSession();
  return owner === null ? undefined : states.get(owner);
}

function stateFor(owner: ClientSession): OwnedEditorSession | undefined {
  return states.get(owner);
}

function nearestSurvivor(
  open: EditorSessionEntry[],
  closed: ReadonlySet<string>,
  active: string | null,
): string | null {
  if (active !== null && !closed.has(active)) {
    return active;
  }
  const activeIndex = open.findIndex((entry) => entry.path === active);
  for (let offset = 1; offset < open.length; offset += 1) {
    const right = open[activeIndex + offset];
    if (right !== undefined && !closed.has(right.path)) {
      return right.path;
    }
    const left = open[activeIndex - offset];
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
  const entry = open.find((candidate) => candidate.path === path);
  return entry === undefined ? null : { path, placement: { viewState: entry.viewState ?? null } };
}

export type Placement =
  | { line: number; column?: number; focus?: boolean }
  | {
      selection: {
        startLineNumber: number;
        startColumn: number;
        endLineNumber: number;
        endColumn: number;
      };
    }
  | { viewState: EditorViewState | null };

export interface ActivateResult {
  path: string;
  placement: Placement;
}

export interface CloseResult {
  disposed: string;
  next: ActivateResult | null;
}

export const editorSession = (): EditorSession | null => selectedState()?.current() ?? null;
export const editorSessionFor = (owner: ClientSession): EditorSession | null =>
  stateFor(owner)?.current() ?? null;
export const pendingLineFor = (owner: ClientSession, path: string): number | undefined =>
  stateFor(owner)?.pendingLine(path);
export function onEditorSessionChanged(owner: ClientSession, listener: () => void): () => void {
  return stateFor(owner)?.subscribeStructure(listener) ?? (() => {});
}
export const openTabs = (): EditorSessionEntry[] => editorSession()?.open ?? [];
export const openTabsFor = (owner: ClientSession): EditorSessionEntry[] =>
  editorSessionFor(owner)?.open ?? [];
export const activePath = (): string | null => editorSession()?.active ?? null;
export const activePathFor = (owner: ClientSession): string | null =>
  editorSessionFor(owner)?.active ?? null;
export const editorBackendId = (): string | null => selectedSession()?.connection.id ?? null;
export const editorOwner = (): string | null => selectedSession()?.address.incarnation ?? null;

export function flushEditorSession(): void {
  selectedState()?.flush();
}

export function flushEditorSessionFor(owner: ClientSession): void {
  stateFor(owner)?.flush();
}

export function openTab(
  path: string,
  opts: {
    line?: number;
    column?: number;
    focus?: boolean;
    preview?: boolean;
    scratch?: boolean;
    kind?: "file" | "web" | "source" | "plan" | "review";
  } = {},
): ActivateResult {
  return (
    selectedState()?.openTab(path, opts) ?? {
      path,
      placement: { line: opts.line ?? 1 },
    }
  );
}

export function openTabFor(
  owner: ClientSession,
  path: string,
  opts: {
    line?: number;
    column?: number;
    focus?: boolean;
    preview?: boolean;
    scratch?: boolean;
    kind?: "file" | "web" | "source" | "plan" | "review";
  } = {},
): ActivateResult {
  return (
    stateFor(owner)?.openTab(path, opts) ?? {
      path,
      placement: { line: opts.line ?? 1 },
    }
  );
}

export const activateTab = (path: string): ActivateResult | null =>
  selectedState()?.activate(path) ?? null;
export const activateTabFor = (owner: ClientSession, path: string): ActivateResult | null =>
  stateFor(owner)?.activate(path) ?? null;
export const closeTab = (path: string): CloseResult | null => selectedState()?.close(path) ?? null;
export const closeTabFor = (owner: ClientSession, path: string): CloseResult | null =>
  stateFor(owner)?.close(path) ?? null;
export const dropReviewTab = (path: string, fallback: string | null): void =>
  selectedState()?.dropReview(path, fallback);
export const dropReviewTabFor = (
  owner: ClientSession,
  path: string,
  fallback: string | null,
): void => stateFor(owner)?.dropReview(path, fallback);
export const convertScratch = (scratchPath: string, savedPath: string): ActivateResult | null =>
  selectedState()?.convertScratch(scratchPath, savedPath) ?? null;
export const convertScratchFor = (
  owner: ClientSession,
  scratchPath: string,
  savedPath: string,
): ActivateResult | null => stateFor(owner)?.convertScratch(scratchPath, savedPath) ?? null;
export const closeMany = (
  predicate: (entry: EditorSessionEntry) => boolean,
): { disposed: string[]; next: ActivateResult | null } =>
  selectedState()?.closeMany(predicate) ?? { disposed: [], next: null };
export const closeManyFor = (
  owner: ClientSession,
  predicate: (entry: EditorSessionEntry) => boolean,
): { disposed: string[]; next: ActivateResult | null } =>
  stateFor(owner)?.closeMany(predicate) ?? { disposed: [], next: null };
export const togglePin = (path: string): void => selectedState()?.togglePin(path);
export const togglePinFor = (owner: ClientSession, path: string): void =>
  stateFor(owner)?.togglePin(path);
export const promote = (path: string): void => selectedState()?.promote(path);
export const promoteFor = (owner: ClientSession, path: string): void =>
  stateFor(owner)?.promote(path);
export const captureViewState = (path: string, viewState: EditorViewState | null): void =>
  selectedState()?.captureViewState(path, viewState);
export const captureReviewFor = (owner: ClientSession, review: ReviewResume): void =>
  stateFor(owner)?.captureReview(review);
export const captureViewStateFor = (
  owner: ClientSession,
  path: string,
  viewState: EditorViewState | null,
): void => stateFor(owner)?.captureViewState(path, viewState);

export const tabOwnerFor = (session: ClientSession, path: string): TabOwner | undefined =>
  stateFor(session)?.tab(path);

export const activeTabFor = (session: ClientSession): TabOwner | undefined => {
  const path = activePathFor(session);
  return path === null ? undefined : tabOwnerFor(session, path);
};
