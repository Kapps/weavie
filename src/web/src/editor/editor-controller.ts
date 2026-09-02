// Owns the Monaco editor lifecycle and diff/review orchestration on App's behalf. Drives the editor host +
// inline-diff layer (editor-host.ts / inline-diff.ts).

import type * as monaco from "monaco-editor";
import { createSignal } from "solid-js";
import {
  type ClientSession,
  isBrowserHostedShell,
  log,
  onSelectedSession,
  registerSessionFeature,
  selectedSession,
} from "../bridge";
import { dismissSplash } from "../splash";
import { mark } from "../startup-timing";
// Type-only (erased at build): the symbol query surface's monaco glue is dynamically imported in start(), so it
// stays in the lazily loaded editor chunk rather than the first-paint entry chunk.
import type {
  FlatSymbol,
  SymbolActions,
  SymbolQueryResult,
  SymbolQuerySource,
} from "../symbols/symbol-match";
import type { CommentProse } from "./comment-prose";
import type { EditorHost } from "./editor-host";
import { samePath } from "./fs-path";
import type { GitBlameController } from "./git-blame";
import type {
  HunkRevert,
  HunkUnkeep,
  InlineDiff,
  InlineDiffActions,
  InlineDiffOptions,
} from "./inline-diff";
import { mediaTypeOf } from "./media/media-types";
import { createNavHistory, type NavHistory } from "./nav-history";
import { setAgentPlan } from "./plan/plan-store";
import { REVEAL_SCROLL } from "./reveal-scroll";
import {
  createReviewStore,
  type ReviewComments,
  type ReviewFile,
  type ReviewFileDiff,
  type ReviewHistory,
  type ReviewOverview,
  type ReviewPresentationMode,
} from "./review/review-store";
import type { ReviseMarks, ReviseRegion } from "./revise-marks";
import {
  type ActivateResult,
  activateTabFor,
  activePath,
  activePathFor,
  closeManyFor,
  closeTabFor,
  convertScratchFor,
  dropReviewTabFor,
  editorSessionFor,
  flushEditorSessionFor,
  openTabFor,
  openTabsFor,
  promoteFor,
  togglePinFor,
} from "./session-store";
import type { EditorSession, EditorSessionEntry } from "./session-types";
import { SESSION_FILE_SCHEME, sessionForUri, sessionUriHostPath } from "./session-uri-owner";

// Only a genuine hang trips this, never a slow cold start: the editor chunk (~750KB of Monaco + workers) plus
// vscode-services init can legitimately run tens of seconds on a loaded machine or across the remote worker hop
// (browser → Runner → worker). 15s misfired there — a slow-but-successful boot got killed and `data-ready` never
// stamped — so it's set well above any real cold start while still bounding an init that truly never settles.
const EDITOR_INIT_MS = 60_000;

export interface EditorControllerDeps {
  /** Surface a debounced save that failed to reach disk. */
  onSaveError: (message: string) => void;
  /** Surface a file that couldn't be opened (read), so a failed open errors loudly instead of a blank tab. */
  onOpenError: (message: string) => void;
  /** Report the file the editor is showing so the browser / title bar can track it. */
  onCurrentFileChanged: (path: string | null) => void;
  /** Reveal an accepted foreground editor destination in the app's active presentation. */
  onDestinationActivated: () => void;
  /** Activate the editor pane and focus its visible overlay; false when Monaco is the visible surface. */
  focusVisibleOverlay: () => boolean;
  /** Confirm discarding unsaved scratch buffers about to be closed (`names`); the single close-path guard. */
  confirmDiscard: (names: string[]) => Promise<boolean>;
  /** Confirm a destructive review action (Revert file / Revert all). Resolves true to proceed. */
  confirm: (options: { title: string; body: string; confirmLabel: string }) => Promise<boolean>;
  /** Prompt in-app for a scratch buffer's save name on a browser-served host (no native Save-As dialog);
   * resolves the chosen workspace-relative name, or null if cancelled. */
  promptScratchName: (suggestedName: string) => Promise<string | null>;
  /** Ask what to do to the selected lines; resolves the instruction, or null if cancelled. */
  promptRevision: (lineCount: number) => Promise<string | null>;
}

interface DiffProposal {
  id: string;
  path: string;
  tabName: string;
  original: string;
  proposed: string;
}

interface SessionProposal extends DiffProposal {
  addedTab: boolean;
  priorActive: string | null;
}

/**
 * Tab operations exposed to commands and the tab strip. Targeted ops default to the active tab when `path` is
 * omitted; the context menu passes the right-clicked tab.
 */
export interface TabActions {
  /** Switch to an already-open tab, restoring its saved view state. */
  activate(path: string): void;
  /** Close a tab (any state — may close a pinned tab when invoked on it explicitly). Default active. */
  close(path?: string): void;
  /** Close all non-pinned tabs. */
  closeAll(): void;
  /** Close every non-pinned tab except `path` (default active). */
  closeOthers(path?: string): void;
  /** Close non-pinned tabs to the left of `path` (default active). */
  closeToLeft(path?: string): void;
  /** Close non-pinned tabs to the right of `path` (default active). */
  closeToRight(path?: string): void;
  /** Pin or unpin a tab (default active); pinning promotes a preview tab and floats it furthest-left. */
  togglePin(path?: string): void;
  /** Promote a preview tab to persistent (default active). */
  promote(path?: string): void;
  /** Activate the next / previous tab in visual order, wrapping. False if there's nothing to step to. */
  next(): boolean;
  prev(): boolean;
  /** Reopen the most recently closed file/web tab. False when there's nothing to reopen. */
  reopenClosed(): boolean;
}

/** Back/forward navigation through visited editor locations, exposed to the Go Back / Go Forward commands. */
export interface NavActions {
  /** Go to the previous location; false when there's nothing behind (so the keybinding falls through). */
  back(): boolean;
  /** Go to the next location; false when there's nothing ahead. */
  forward(): boolean;
  /** Whether a previous location is available (reactive). */
  canBack(): boolean;
  /** Whether a next location is available (reactive). */
  canForward(): boolean;
}

export interface EditorController {
  /** Revise the selected lines: prompt for an instruction, then hand the region to the host. */
  reviseSelection(): void;
  /** Loads the editor chunk and brings up the editor in `container`; fades the splash when settled. */
  start(container: HTMLElement): void;
  /** Opens a file (preview tab when `preview`), replaying once the editor chunk has loaded (last wins). */
  openFile(path: string, line: number, preview?: boolean): void;
  /** Opens an http(s) URL as a web (iframe) tab in the editor tab strip. */
  openWebTab(url: string): void;
  /** Opens a fetched source doc (Notion) as a source (shadow-root) tab in the editor tab strip, keyed by its target. */
  openSourceTab(target: string): void;
  /** Focuses the editor (for focus-pane). */
  focusEditor(): void;
  /**
   * Opens a find-in-files hit in the preview tab, landing the cursor at line:column. `focus: false` reveals
   * without stealing focus — the panel's live preview while arrowing through results.
   */
  openMatch(path: string, line: number, column: number, focus: boolean): void;
  /** Focuses the editor and triggers a Monaco action by id (e.g. the editor right-click Copy/Cut/Paste);
   * false when no editor is mounted. */
  triggerAction(actionId: string): boolean;
  /** New File: asks the host to create a scratch buffer, which comes back as an open-file with `scratch`. */
  newFile(): void;
  /** Save the active editor: a scratch buffer prompts for a name; a real file is already autosaved. */
  save(): boolean;
  /**
   * Flushes every dirty working copy to the active backend and resolves once they land — called before a
   * cross-backend session switch so edits persist on their own host. Resolves immediately when unmounted.
   */
  flushDirty(): Promise<void>;
  /** Flushes one exact session's editor state and dirty working copies before its backend is torn down. */
  flushSession(session: ClientSession): Promise<void>;
  /** Open the review overview, or a specific file when a path is supplied. */
  openReview(session: ClientSession, path: string | undefined, line: number | undefined): boolean;
  /**
   * Opens the blame popover for the cursor's line, or reports why that line has no commit behind it. False
   * only when no editor is mounted, so the command declines rather than appearing to do nothing.
   */
  showBlameAtCursor(): boolean;
  /** The active file's current working-copy text (reactive), for the Preview overlay; "" when none. */
  activeContent(): string;
  /** Whether an inline openDiff review is showing (reactive), so Preview suspends rather than hiding it. */
  reviewActive(): boolean;
  /** How many files are pending post-turn review (reactive), so the empty-state pane can surface a review cue
   * when no file is open. */
  parkedReviewCount(): number;
  readonly review: {
    mode(): ReviewPresentationMode;
    overview(): ReviewOverview;
    /**
     * Resolve a changed file's working copy for the unified review's per-file editor, on a reference
     * independent of any tab's. Rejects when the editor host isn't up, so the section can say so.
     */
    openCopy(session: ClientSession, path: string): Promise<monaco.editor.ITextModel>;
    /** Release every working copy the unified review holds (its surface unmounted). */
    releaseCopies(): void;
    toggleMode(session: ClientSession): boolean;
    setCursor(session: ClientSession, path: string, line: number): void;
    revert(session: ClientSession): boolean;
    keepFile(session: ClientSession, path: string | undefined): boolean;
    revertFile(session: ClientSession, path: string | undefined): boolean;
    keepAll(session: ClientSession): boolean;
    revertAll(session: ClientSession): boolean;
    undoKeep(session: ClientSession): boolean;
    undoRevert(session: ClientSession): boolean;
    redo(session: ClientSession): boolean;
  };
  readonly inline: InlineDiffActions;
  readonly tabs: TabActions;
  readonly nav: NavActions;
  /** The omnibar's Go-to-Symbol surface: query document/workspace symbols and live-preview/commit the jump. */
  readonly symbols: SymbolActions;
  dispose(): void;
}

export function createEditorController(deps: EditorControllerDeps): EditorController {
  // host + inlineDiff are set once the editor chunk loads and the editor is created (see start).
  let host: EditorHost | undefined;
  let inlineDiff: InlineDiff | undefined;
  let commentProse: CommentProse | undefined;
  let gitBlame: GitBlameController | undefined;
  let reviseMarks: ReviseMarks | undefined;
  // Captured from the dynamic inline-diff import in start(); used by the show-diff handler, which can
  // only fire once the editor host (and thus this import) is up.
  let firstChangedLine: ((original: string, modified: string) => number) | undefined;
  let initTimer: number | undefined;
  // Disposables for the content/model listeners that feed activeContent (the live Preview text).
  let contentSubs: { dispose(): void }[] = [];
  let editorMounted = false;
  const reviews = createReviewStore();
  const reviewProposals = new WeakMap<ClientSession, SessionProposal>();
  const publishSelected = (feature: string, name: string, payload: unknown): void => {
    selectedSession()?.feature(feature).publish(name, payload);
  };
  const rebindSession = async (session: ClientSession): Promise<void> => {
    const editorHost = host;
    if (editorHost === undefined || selectedSession() !== session) {
      return;
    }
    clearPresentedProposal();
    // rebindSession releases the outgoing model synchronously before its first await. Concurrent rebinds are
    // latest-wins inside EditorHost, so selection can never leave an old session editable while a read settles.
    await editorHost.rebindSession(session);
    if (selectedSession() === session) {
      deps.onCurrentFileChanged(activePath());
      renderReviewState(session);
    }
  };
  // The active file's working-copy text, kept live off the editor model so Preview renders edits/reloads.
  const [activeContent, setActiveContent] = createSignal("");
  // Whether an inline openDiff review currently occupies the editor, so the Preview overlay suspends over it.
  const [reviewActive, setReviewActive] = createSignal(false);
  // The openDiff under inline review (at most one live, since openDiff blocks). `reviewUri` keys the transient
  // review model the inline diff is rendered over.
  let activeReview:
    | {
        session: ClientSession;
        id: string;
        path: string;
        original: string;
        reviewUri: string | undefined;
        // Tab opened purely to show the proposal; on reject, drop it and return to `priorActive`.
        addedTab: boolean;
        // Tab active before the review, restored if an `addedTab` is dropped on reject/cancel.
        priorActive: string | null;
      }
    | undefined;

  // Translate "active tab changed" → "swap the editor's model": the tab store owns the set, the host owns Monaco.
  // Resolves once the (async) model swap has settled — nav history awaits this to know when a back/forward step
  // has landed, so don't drop the return value: mid-swap the editor still reports the old file (see nav-history).
  const applyActive = (session: ClientSession, result: ActivateResult): Promise<void> => {
    if (selectedSession() !== session) {
      return Promise.resolve();
    }
    const editorHost = host;
    if (editorHost === undefined) {
      // The store already owns this activation; start() rebinds it once the lazy editor host is ready.
      return Promise.resolve();
    }
    // An overlay tab has no Monaco model: leave the editor host untouched (App overlays it) and never read the
    // path as a file. Same for a media (image/video) file tab —
    // reading it as a working copy would decode binary as UTF-8 and autosave could write the mojibake back.
    const activeKind = openTabsFor(session).find((tab) => tab.path === result.path)?.kind;
    deps.onCurrentFileChanged(activeKind === "plan" ? null : result.path);
    if (
      activeKind === "web" ||
      activeKind === "source" ||
      activeKind === "plan" ||
      mediaTypeOf(result.path) !== null
    ) {
      host?.clear();
      return Promise.resolve();
    }
    // Don't clobber an in-progress review: the reviewed file is active, but the editor shows the transient
    // review model; re-showing the working copy would drop the diff. resolveReview → endReview restores it.
    if (activeReview !== undefined && samePath(activeReview.path, result.path)) {
      return Promise.resolve();
    }
    // If the file can't be read, the editor never swaps its model — close this tab rather than leave it active
    // over a stale/blank pane, and fall back to a surviving neighbor (or clear).
    return editorHost.show(session, result.path, result.placement).then((ok) => {
      if (!ok) {
        rollbackFailedOpen(session, result.path);
      }
    });
  };

  // Drop a tab whose open failed (no working copy to release) and, if it was active, switch to its neighbor. A
  // cascade is fine: an unreadable neighbor rolls back in turn until a readable tab or empty pane is reached.
  const rollbackFailedOpen = (session: ClientSession, path: string): void => {
    const wasActive = activePathFor(session);
    const result = closeTabFor(session, path);
    if (result === null) {
      return;
    }
    if (path === wasActive) {
      applyOrClear(session, result.next);
    }
  };

  const focusEditorSurface = (): void => {
    if (!deps.focusVisibleOverlay()) {
      host?.editor.focus();
    }
  };

  const activateDestinationFor = (session: ClientSession): boolean => {
    if (selectedSession() !== session) {
      return false;
    }
    reviews.leaveUnified(session);
    deps.onDestinationActivated();
    return true;
  };

  const openFileFor = (
    session: ClientSession,
    path: string,
    line: number,
    preview = false,
    scratch = false,
  ): void => {
    const result = openTabFor(session, path, { line, preview, scratch });
    if (activateDestinationFor(session) && host !== undefined) {
      void applyActive(session, result).then(focusEditorSurface);
    }
  };

  const openFile = (path: string, line: number, preview = false, scratch = false): void => {
    const session = selectedSession();
    if (session !== null) {
      openFileFor(session, path, line, preview, scratch);
    }
  };

  // The document/workspace symbol query surface (monaco glue), captured once the editor chunk loads in start().
  let symbolSource: SymbolQuerySource | undefined;
  // The active editor's scroll/cursor captured before the first preview reveal, restored if the user dismisses
  // Go-to-Symbol without committing. Undefined when no preview is in flight.
  let previewReturn: monaco.editor.ICodeEditorViewState | null | undefined;

  const isActiveFile = (path: string): boolean => samePath(path, activePath() ?? "");

  // Reveal + select a symbol in place in the REAL editor as the omnibar selection moves, but only when it lives in
  // the file already showing — document-symbol (@) rows, and the occasional workspace (#) hit in the current file.
  // A symbol in another file reveals only on commit: opening files just to skim whole-repo results would churn the
  // editor more than it helps. The first reveal snapshots the view so cancelPreview can restore it.
  const previewSymbol = (sym: FlatSymbol): void => {
    if (host === undefined || !isActiveFile(sym.path)) {
      return;
    }
    if (previewReturn === undefined) {
      previewReturn = host.editor.saveViewState();
    }
    host.editor.setSelection(sym.range);
    host.editor.revealRangeInCenterIfOutsideViewport(sym.range, REVEAL_SCROLL);
  };

  // Dismissed without choosing: restore the pre-preview scroll/cursor.
  const cancelPreview = (): void => {
    const viewState = previewReturn;
    previewReturn = undefined;
    if (viewState != null && host !== undefined) {
      host.editor.restoreViewState(viewState);
    }
  };

  // Committed: keep the jump. Re-reveal in place, or open the file as a real (non-preview) tab so it sticks. Self
  // sufficient — works whether or not a preview fired (Enter on an unarrowed selection still lands).
  const commitPreview = (sym: FlatSymbol): void => {
    previewReturn = undefined;
    const session = selectedSession();
    if (host === undefined || session === null) {
      return;
    }
    if (isActiveFile(sym.path)) {
      activateDestinationFor(session);
      host.editor.setSelection(sym.range);
      host.editor.revealRangeInCenterIfOutsideViewport(sym.range, REVEAL_SCROLL);
      host.editor.focus();
    } else {
      openFile(sym.path, sym.range.startLineNumber);
    }
  };

  const noSymbols = (): Promise<SymbolQueryResult> =>
    Promise.resolve({ providerAvailable: false, items: [] });
  const symbols: SymbolActions = {
    documentSymbols: () => symbolSource?.documentSymbols() ?? noSymbols(),
    workspaceSymbols: (query, signal) =>
      symbolSource?.workspaceSymbols(query, signal) ?? noSymbols(),
    preview: previewSymbol,
    cancelPreview,
    commitPreview,
  };

  // Browser-style back/forward over visited editor locations. navigateTo reuses the open/activate path
  // (openTab activates an already-open tab or opens it, then applyActive reveals the line) and returns its
  // settle promise, so nav history can suppress records until the swap lands.
  const navHistories = new WeakMap<ClientSession, NavHistory>();
  const [navRevision, setNavRevision] = createSignal(0);
  const navHistoryFor = (session: ClientSession): NavHistory => {
    const existing = navHistories.get(session);
    if (existing !== undefined) {
      return existing;
    }
    const created = createNavHistory((loc) => {
      if (selectedSession() !== session || host === undefined) {
        return Promise.resolve();
      }
      activateDestinationFor(session);
      return applyActive(session, openTabFor(session, loc.path, { line: loc.line }));
    });
    navHistories.set(session, created);
    return created;
  };

  // Record where the editor settles (active file + cursor line) as a navigation point, debounced like the
  // view-state snapshot so only the resting position is logged — not the brief top-of-file the editor sits at
  // mid-swap before a reveal. Only real file models: overlay (web/source) tabs and the transient review model
  // aren't navigable locations.
  let navTimer: ReturnType<typeof setTimeout> | undefined;
  const recordNavLocation = (): void => {
    const model = host?.editor.getModel();
    const position = host?.editor.getPosition();
    if (model == null || position == null || model.uri.scheme !== SESSION_FILE_SCHEME) {
      return;
    }
    const session = sessionForUri(model.uri);
    if (session === undefined) {
      return;
    }
    // uriHostPath, not fsPath: a back-navigation re-opens this path as a tab, which must stay host-native.
    navHistoryFor(session).record({
      path: sessionUriHostPath(model.uri),
      line: position.lineNumber,
    });
    setNavRevision((revision) => revision + 1);
  };
  const scheduleRecordNav = (): void => {
    if (navTimer !== undefined) {
      clearTimeout(navTimer);
    }
    navTimer = setTimeout(recordNavLocation, 150);
  };

  // Open an http(s) URL as a web (iframe) tab. No Monaco model / working copy — App renders an iframe over the
  // editor host when this tab is active. Independent of the editor chunk, so it works before Monaco is up.
  const openWebTab = (url: string): void => {
    const session = selectedSession();
    if (session !== null) {
      activateDestinationFor(session);
      void applyActive(session, openTabFor(session, url, { kind: "web" }));
    }
  };

  // Open a fetched source doc (Notion) as a source tab, keyed by its target. No Monaco model — App overlays the
  // SourceView shadow-root render over the editor host when this tab is active; SourceView reads the html by target.
  const openSourceTab = (target: string): void => {
    const session = selectedSession();
    if (session !== null) {
      activateDestinationFor(session);
      void applyActive(session, openTabFor(session, target, { kind: "source" }));
    }
  };

  // Switch the editor off a closing tab before its working copy is released, else clear to an empty pane.
  const applyOrClear = (session: ClientSession, next: ActivateResult | null): void => {
    if (selectedSession() !== session) {
      return;
    }
    if (next !== null) {
      void applyActive(session, next);
    } else {
      host?.clear();
      deps.onCurrentFileChanged(null);
    }
  };

  const basename = (path: string): string => path.split(/[\\/]/).pop() ?? path;

  // True if `path` is a scratch (untitled) buffer holding real content — the only tab whose close can lose
  // unsaved work, since real files autosave.
  const isDirtyScratch = (session: ClientSession, path: string): boolean => {
    const entry = openTabsFor(session).find((tab) => tab.path === path);
    if (entry?.scratch !== true) {
      return false;
    }
    return (host?.contentOf(session, path) ?? "").trim().length > 0;
  };

  // The one guard every close path runs through: if any doomed tab is an unsaved scratch, confirm once before
  // closing. Resolves true to proceed, false to abort. Empty scratches need no confirm.
  const guardDiscard = async (session: ClientSession, doomed: string[]): Promise<boolean> => {
    const dirty = doomed.filter((path) => isDirtyScratch(session, path));
    if (dirty.length === 0) {
      return true;
    }
    return deps.confirmDiscard(dirty.map(basename));
  };

  // Release a closed tab's working copy. A scratch tab is discarded — its model is dropped without flushing
  // and the host deletes its temp file; a real file flushes its pending save first.
  const releaseClosed = (session: ClientSession, path: string, scratch: boolean): void => {
    if (scratch) {
      host?.closeFile(session, path, true);
      session.feature("editor").publish("discardScratch", { path });
    } else {
      host?.closeFile(session, path);
    }
  };

  // Close every tab matching `predicate` (closeMany skips pinned). Guards unsaved scratch work first (one
  // confirm for the batch), switches off a doomed active tab, then releases each closed working copy.
  const closeBy = async (predicate: (entry: EditorSessionEntry) => boolean): Promise<void> => {
    const session = selectedSession();
    if (session === null) {
      return;
    }
    const doomed = openTabsFor(session).filter(
      (entry) => predicate(entry) && entry.pinned !== true,
    );
    if (
      doomed.length === 0 ||
      !(await guardDiscard(
        session,
        doomed.map((entry) => entry.path),
      ))
    ) {
      return;
    }
    const scratchPaths = new Set(
      doomed.filter((entry) => entry.scratch === true).map((entry) => entry.path),
    );
    // Overlay tabs have no working copy to release.
    const overlayPaths = new Set(
      doomed
        .filter((entry) => entry.kind === "web" || entry.kind === "source" || entry.kind === "plan")
        .map((entry) => entry.path),
    );
    const wasActive = activePathFor(session);
    const result = closeManyFor(session, predicate);
    if (result.disposed.length === 0) {
      return;
    }
    if (wasActive !== null && result.disposed.includes(wasActive)) {
      applyOrClear(session, result.next);
    }
    for (const path of result.disposed) {
      if (!overlayPaths.has(path)) {
        releaseClosed(session, path, scratchPaths.has(path));
      }
    }
    for (const entry of doomed) {
      if (result.disposed.includes(entry.path)) {
        recordClosed(session, entry);
      }
    }
  };

  // Recently-closed file/web/source tabs, most-recent last, so Reopen Closed Editor (Ctrl+Shift+T) can bring one
  // back. Scratch and virtual plan tabs are excluded because neither is a persistent document.
  const closedTabs = new WeakMap<
    ClientSession,
    { path: string; kind: EditorSessionEntry["kind"] }[]
  >();
  const CLOSED_TABS_LIMIT = 25;
  const recordClosed = (session: ClientSession, entry: EditorSessionEntry): void => {
    if (entry.scratch === true || entry.kind === "plan") {
      return;
    }
    const entries = closedTabs.get(session) ?? [];
    entries.push({ path: entry.path, kind: entry.kind });
    if (entries.length > CLOSED_TABS_LIMIT) {
      entries.shift();
    }
    closedTabs.set(session, entries);
  };
  // Reopen the most recently closed tab that isn't already open again; skip stale records for tabs reopened by
  // other means. Declines (returns false) when there's nothing to reopen, so Ctrl+Shift+T falls through.
  const reopenClosed = (): boolean => {
    const session = selectedSession();
    if (session === null) {
      return false;
    }
    const entries = closedTabs.get(session) ?? [];
    while (entries.length > 0) {
      const entry = entries.pop();
      if (
        entry === undefined ||
        openTabsFor(session).some((tab) => samePath(tab.path, entry.path))
      ) {
        continue;
      }
      if (entry.kind === "web") {
        openWebTab(entry.path);
      } else if (entry.kind === "source") {
        openSourceTab(entry.path);
      } else {
        openFile(entry.path, 1);
      }

      return true;
    }

    return false;
  };

  const closeTabForSession = async (session: ClientSession, path: string): Promise<void> => {
    // `path` may arrive from the host (Claude's close_tab) spelled differently than the stored key, so match
    // by normalized identity, then operate on the entry's own stored path downstream.
    const entry = openTabsFor(session).find((tab) => samePath(tab.path, path));
    if (entry === undefined || !(await guardDiscard(session, [entry.path]))) {
      return;
    }
    const scratch = entry.scratch === true;
    const wasActive = activePathFor(session);
    const result = closeTabFor(session, entry.path);
    if (result === null) {
      return;
    }
    recordClosed(session, entry);
    if (entry.path === wasActive) {
      applyOrClear(session, result.next);
    }
    // Overlay tabs have no working copy / Monaco model to release.
    if (entry.kind !== "web" && entry.kind !== "source" && entry.kind !== "plan") {
      releaseClosed(session, result.disposed, scratch);
    }
  };

  // Step through tabs in visual order, wrapping. Returns false (so the keybinding falls through to the editor)
  // when there's nothing to step to.
  const step = (delta: number): boolean => {
    const session = selectedSession();
    if (session === null) {
      return false;
    }
    const list = openTabsFor(session);
    if (list.length < 2) {
      return false;
    }
    const idx = list.findIndex((tab) => tab.path === activePathFor(session));
    if (idx === -1) {
      return false;
    }
    const target = list[(idx + delta + list.length) % list.length];
    if (target === undefined) {
      return false;
    }
    const result = activateTabFor(session, target.path);
    if (result !== null) {
      activateDestinationFor(session);
      void applyActive(session, result);
    }
    return true;
  };

  const closeRelative = (path: string, side: "left" | "right"): void => {
    const session = selectedSession();
    if (session === null) {
      return;
    }
    const list = openTabsFor(session);
    const ti = list.findIndex((tab) => tab.path === path);
    if (ti === -1) {
      return;
    }
    const slice = side === "left" ? list.slice(0, ti) : list.slice(ti + 1);
    if (slice.length === 0) {
      return;
    }
    const targets = new Set(slice.map((tab) => tab.path));
    void closeBy((entry) => targets.has(entry.path));
  };

  // Resolve a targeted op's subject: the explicit path (context menu) or the active tab (keyboard / palette).
  const target = (path: string | undefined): string | null => path ?? activePath();

  const tabs: TabActions = {
    activate: (path) => {
      const session = selectedSession();
      const result = session === null ? null : activateTabFor(session, path);
      if (session !== null && result !== null) {
        activateDestinationFor(session);
        void applyActive(session, result);
      }
    },
    close: (path) => {
      const subject = target(path);
      const session = selectedSession();
      if (session !== null && subject !== null) {
        void closeTabForSession(session, subject);
      }
    },
    closeAll: () => void closeBy(() => true),
    closeOthers: (path) => {
      const subject = target(path);
      if (subject !== null) {
        void closeBy((entry) => entry.path !== subject);
      }
    },
    closeToLeft: (path) => {
      const subject = target(path);
      if (subject !== null) {
        closeRelative(subject, "left");
      }
    },
    closeToRight: (path) => {
      const subject = target(path);
      if (subject !== null) {
        closeRelative(subject, "right");
      }
    },
    togglePin: (path) => {
      const subject = target(path);
      const session = selectedSession();
      if (session !== null && subject !== null) {
        togglePinFor(session, subject);
      }
    },
    promote: (path) => {
      const subject = target(path);
      const session = selectedSession();
      if (session !== null && subject !== null) {
        promoteFor(session, subject);
      }
    },
    next: () => step(1),
    prev: () => step(-1),
    reopenClosed: () => reopenClosed(),
  };

  const resolveReview = (keep: boolean): void => {
    const review = activeReview;
    if (review === undefined) {
      return;
    }
    activeReview = undefined;
    setReviewActive(false);
    // endReview returns the proposal's final content (which Claude writes to disk on keep) and restores the
    // editor off the transient review model. The review never dirtied the working copy.
    const finalContents = host?.endReview(review.session, review.path, keep, review.original) ?? "";
    if (review.reviewUri !== undefined) {
      inlineDiff?.clearByUri(review.reviewUri);
    }
    // A rejected proposal whose tab was opened just to review it: drop it and fall back to the previously
    // active tab (a store-only fixup; endReview already restored the editor). A kept file stays open.
    if (!keep && review.addedTab) {
      dropReviewTabFor(review.session, review.path, review.priorActive);
    }
    reviewProposals.delete(review.session);
    if (selectedSession() === review.session) {
      deps.onCurrentFileChanged(activePathFor(review.session));
    }
    review.session.feature("editor").publish("resolveDiff", {
      id: review.id,
      kept: keep,
      finalContents: keep ? finalContents : "",
    });
  };

  // Brings up the editor, holding the splash until a deterministic outcome — editor ready or a real failure
  // (chunk load, crash, or an init that never settles within EDITOR_INIT_MS) — so the reveal shows a settled UI.
  const start = (container: HTMLElement): void => {
    const editorReady = import("./editor-host").then(({ createEditorHost }) =>
      createEditorHost(
        container,
        deps.onSaveError,
        deps.onOpenError,
        ({ session, path, line }) => navHistoryFor(session).record({ path, line }),
        ({ session, path, selection }) => {
          const result = openTabFor(session, path, { preview: true });
          result.placement = selection === undefined ? { line: 1 } : { selection };
          // A jump out of a unified-review section (go-to-definition, peek) lands in the file editor, which the
          // overview would otherwise cover.
          reviews.leaveUnified(session);
          activateDestinationFor(session);
          void applyActive(session, result);
        },
      ),
    );
    const initDeadline = new Promise<never>((_, reject) => {
      initTimer = window.setTimeout(
        () => reject(new Error(`editor init did not settle within ${EDITOR_INIT_MS}ms`)),
        EDITOR_INIT_MS,
      );
    });
    void Promise.race([editorReady, initDeadline])
      .then(async (created) => {
        host = created;
        // inline-diff + comment-prose pull Monaco; import them here (the chunk is already loaded by the
        // editor host above) so they stay off the first-paint entry chunk.
        const [diff, prose, symbolMod, blame, marks] = await Promise.all([
          import("./inline-diff"),
          import("./comment-prose"),
          import("../symbols/symbol-source"),
          import("./git-blame"),
          import("./revise-marks"),
        ]);
        symbolSource = symbolMod.createSymbolSource(created.editor);
        firstChangedLine = diff.firstChangedLine;
        inlineDiff = diff.createInlineDiff(created.editor);
        // Review undo/redo is session-global (not tied to a file), so its post-callbacks are bound once. `kind`
        // targets the type-split chords; the generic Undo (toolbar) omits it.
        inlineDiff.bindHistory({
          onUndoKeep: () => publishSelected("review", "undo", { kind: "keep" }),
          onUndoRevert: () => publishSelected("review", "undo", { kind: "revert" }),
          onUndoLast: () => publishSelected("review", "undo", {}),
          onRedo: () => publishSelected("review", "redo", {}),
        });
        // Track the active model's text so the Preview overlay renders live (edits, Claude writes, reloads).
        const syncContent = (): void => {
          setActiveContent(created.editor.getModel()?.getValue() ?? "");
        };
        contentSubs = [
          created.editor.onDidChangeModelContent(() => syncContent()),
          created.editor.onDidChangeModel(() => {
            syncContent();
            scheduleRecordNav();
          }),
          // A cursor jump (or a model swap's reveal) records a navigation point for back/forward.
          created.editor.onDidChangeCursorPosition(() => scheduleRecordNav()),
        ];
        syncContent();
        // Suspended over a model with a live inline diff so a collapsed comment never hides a changed line.
        commentProse = prose.createCommentProse(created.editor, {
          isBlocked: (uri) => inlineDiff?.hasDiffForUri(uri) ?? false,
        });
        reviseMarks = marks.createReviseMarks(created.editor, {
          activePath: () => {
            const current = created.editor.getModel();
            return current === null || current.uri.scheme !== SESSION_FILE_SCHEME
              ? null
              : sessionUriHostPath(current.uri);
          },
        });
        gitBlame = blame.createGitBlame(created.editor);
        const session = selectedSession();
        if (session !== null) {
          await rebindSession(session);
        }
        // Reflect whatever file the editor ended up showing (replayed pending-open or hot-reload restore).
        const model = created.editor.getModel();
        if (model !== null && model.uri.scheme === SESSION_FILE_SCHEME) {
          deps.onCurrentFileChanged(sessionUriHostPath(model.uri));
        }
        // Deterministic "editor is usable" signal: the shell now reveals before the editor chunk settles
        // (App defers start past first paint), so tests and any editor-gated UI wait on this, not on the splash.
        container.setAttribute("data-ready", "true");
        editorMounted = true;
        mark("editor-ready");
      })
      .catch((error: unknown) => {
        log("error", `editor init failed: ${String(error)}`);
        // The pane is now dead (host stays undefined, every openFile silently queues), so tell the user
        // rather than leave a blank editor that swallows clicks.
        deps.onOpenError("The editor failed to load. Reload the window to try again.");
      })
      .finally(() => {
        window.clearTimeout(initTimer);
        dismissSplash();
      });
  };

  // Open a review file on its first change as a preview tab (so ← / → reuses one tab); re-requests its turn-diff
  // so applied markers render even if the push was missed.
  const openReviewFile = (session: ClientSession, file: ReviewFile, line: number): void => {
    if (selectedSession() !== session) {
      return;
    }
    reviews.enterFile(session, { path: file.path, line });
    openFileFor(session, file.path, line, true);
    session.feature("review").publish("showFile", { path: file.path });
  };

  const showUnifiedReview = (session: ClientSession): boolean => {
    if (selectedSession() !== session) {
      return false;
    }
    const state = reviews.board(session);
    if (state.files.length === 0) {
      return false;
    }
    let cursor = state.cursor;
    if (state.mode === "file") {
      const current = activePathFor(session);
      const file = state.files.find(
        (candidate) => current !== null && samePath(candidate.summary().path, current),
      );
      if (file !== undefined) {
        const summary = file.summary();
        cursor = {
          path: summary.path,
          line: host?.editor.getPosition()?.lineNumber ?? summary.line,
        };
      }
    }
    for (const path of reviews.enterUnified(session, cursor)) {
      session.feature("review").publish("showFile", { path });
    }
    deps.onDestinationActivated();
    return true;
  };

  // Monotonic revision of the published review set.
  let reviewRev = 0;
  // Reflect the review set onto the inline-diff's parked navigator: it surfaces (parked at "change 0", editor
  // untouched) whenever files are pending and none is in view, so review is visible the moment changes land —
  // stepping in (a nav key) opens the first change. Called wherever the review board changes.
  const updateParkedReview = (session: ClientSession | null): void => {
    const state = session === null ? null : reviews.board(session);
    const files = state?.files.map((file) => file.summary()) ?? [];
    const label = state?.label ?? "";
    // Publish the live review-walk set for e2e / diagnostics (read-only) — a failed PR-switch test attaches
    // exactly which files the navigator holds, so a leaked cross-PR mix is visible without walking it. `rev`
    // is a monotonic counter bumped on every change so a test can detect quiescence exactly (poll-sampling
    // the file list alone can miss a fast bounce during a rapid switch storm's push drain).
    reviewRev += 1;
    window.__WEAVIE_REVIEW__ = {
      files: files.map((file) => file.path),
      label,
      rev: reviewRev,
    };
    inlineDiff?.setParkedReview(
      files.length > 0
        ? {
            fileCount: files.length,
            ...(label !== "" ? { label } : {}),
            stepIn: () => {
              const first = files[0];
              if (session !== null && first !== undefined) {
                openReviewFile(session, first, first.line);
              }
            },
          }
        : undefined,
    );
  };

  const resetPresentedReview = (): void => {
    inlineDiff?.clearAll();
    inlineDiff?.setReviewHistory({
      canUndo: false,
      canUndoKeep: false,
      canUndoRevert: false,
      canRedo: false,
    });
    commentProse?.refresh();
  };

  // Step the file axis of the review walk: open the neighbour (wrapping) at its first change. Returns false
  // (so Ctrl+Left/Right keep Win/Linux word-nav) when there's no multi-file review. An active file that fell
  // OUT of the set (a session switch's in-flight rebind briefly leaves a stale tab on screen) re-enters at the
  // first file — a nav key pressed at a live review toolbar must never silently no-op.
  const stepReviewFile = (delta: number): boolean => {
    const session = selectedSession();
    if (session === null) {
      return false;
    }
    const files = reviews.board(session).files.map((file) => file.summary());
    if (files.length < 2) {
      return false;
    }
    const current = activePath();
    const idx = current === null ? -1 : files.findIndex((file) => samePath(file.path, current));
    const next = idx === -1 ? files[0] : files[(idx + delta + files.length) % files.length];
    if (next === undefined) {
      return false;
    }
    openReviewFile(session, next, next.line);
    return true;
  };

  // A file's diff just cleared (its last hunk was kept or reverted) while other changed files remain under
  // review: open the next changed file (wrapping, on its first change) so the toolbar follows the review
  // instead of vanishing. Only called when more than one file remains; the kept/reverted file is skipped
  // since the host drops it from the review set right after.
  const advanceToNextPendingFile = (session: ClientSession, fromPath: string): void => {
    const files = reviews.board(session).files.map((file) => file.summary());
    const idx = files.findIndex((file) => samePath(file.path, fromPath));
    const start = idx === -1 ? 0 : idx;
    for (let step = 1; step <= files.length; step++) {
      const candidate = files[(start + step) % files.length];
      if (candidate !== undefined && !samePath(candidate.path, fromPath)) {
        openReviewFile(session, candidate, candidate.line);
        return;
      }
    }
  };

  // Flush the file's pending save (so the host reverts from current disk content), then run `send`. Both the
  // per-hunk and whole-file reverts go through this so the host never races a debounced write. A failed flush
  // means the revert would act against stale disk content, so surface it and abort rather than misapply silently.
  const afterFlush = (session: ClientSession, path: string, send: () => void): void => {
    const flushed = host?.flush(session, path);
    if (flushed === undefined) {
      send();
      return;
    }
    flushed.then(send, (error: unknown) => {
      deps.onSaveError(
        `Couldn't save ${basename(path)} before reverting — revert aborted: ${String(error)}`,
      );
    });
  };

  // Ask the host to revert just this hunk on disk. The host re-emits the file's diff (or an fs-change removal
  // for a created file emptied by the revert), which re-renders without the reverted hunk.
  const revertHunk = (session: ClientSession, path: string, hunk: HunkRevert): void => {
    afterFlush(session, path, () =>
      session.feature("review").publish("revertHunk", { path, ...hunk }),
    );
  };

  // Keep just this hunk: the host advances its review baseline over it (no disk write) so it drops from the
  // pending diff for good. Flush first so the host's guardText check sees the same disk content the web does.
  const keepHunk = (session: ClientSession, path: string, hunk: HunkRevert): void => {
    afterFlush(session, path, () =>
      session.feature("review").publish("keepHunk", { path, ...hunk }),
    );
  };

  // Un-keep just this faded hunk: the host splices the accepted-anchor lines back into the review baseline, so it
  // returns to the bright pending band. No disk read (the guard is against Core's review baseline), so no flush.
  const unkeepHunk = (session: ClientSession, path: string, hunk: HunkUnkeep): void => {
    session.feature("review").publish("unkeepHunk", { path, ...hunk });
  };

  // Keep every change in one file: the host advances its review baseline to current, so the file leaves the
  // review set for good. No confirm — keeping is non-destructive.
  const keepFile = (session: ClientSession, path: string): void => {
    afterFlush(session, path, () => session.feature("review").publish("keepFile", { path }));
  };

  const pendingReviewFile = (
    session: ClientSession,
    path: string | undefined,
  ): ReviewFile | null => {
    const target = path ?? activePathFor(session);
    const file = reviews
      .board(session)
      .files.find((candidate) => target !== null && samePath(candidate.summary().path, target));
    const diff = file?.diff();
    return file === undefined ||
      diff === null ||
      diff === undefined ||
      diff.baseline === diff.current
      ? null
      : file.summary();
  };

  // Revert every change in one file to its turn baseline on disk, after a confirm (the host restores the file
  // wholesale and re-emits its now-empty diff + the trimmed review set).
  const revertFile = (session: ClientSession, path: string): void => {
    void deps
      .confirm({
        title: "Revert file?",
        body: `Discard all changes to "${basename(path)}" and restore it to before this turn? You can undo this afterward.`,
        confirmLabel: "Revert file",
      })
      .then((ok) => {
        if (ok) {
          afterFlush(session, path, () =>
            session.feature("review").publish("revertFile", { path }),
          );
        }
      });
  };

  // Revert the whole turn (revert all), after a confirm — the host reverts every touched file to its baseline.
  const revertAllFor = (session: ClientSession): void => {
    const count = reviews.board(session).files.length;
    void deps
      .confirm({
        title: "Revert all changes?",
        body: `Discard every change from this turn${count > 1 ? ` across ${count} files` : ""}? You can undo this afterward.`,
        confirmLabel: "Revert all",
      })
      .then((ok) => {
        if (ok) {
          session.feature("review").publish("revertAll", {});
        }
      });
  };

  const tryRevertAll = (session: ClientSession): boolean => {
    if (reviews.board(session).files.length === 0) {
      return false;
    }
    revertAllFor(session);
    return true;
  };

  const undoReview = (session: ClientSession, kind: "keep" | "revert"): boolean => {
    const history = reviews.board(session).history;
    if (kind === "keep" ? !history.canUndoKeep : !history.canUndoRevert) {
      return false;
    }
    session.feature("review").publish("undo", { kind });
    return true;
  };

  const redoReview = (session: ClientSession): boolean => {
    if (!reviews.board(session).history.canRedo) {
      return false;
    }
    session.feature("review").publish("redo", {});
    return true;
  };

  // The Comment/Reply actions for a PR file under review (nothing for a plain turn file), merged into the applied
  // diff so commenting coexists with Accept/Reject on the one toolbar. `number` is the PR to post against.
  const prCommentActions = (
    session: ClientSession,
    path: string,
  ): Pick<InlineDiffOptions, "comments" | "onAddComment" | "onReply"> => {
    const pr = reviews
      .board(session)
      .files.find((file) => samePath(file.summary().path, path))
      ?.comments();
    if (pr === null || pr === undefined) {
      return {};
    }
    return {
      comments: pr.comments,
      onAddComment: (line, body) =>
        session.feature("review").publish("addComment", {
          number: pr.number,
          path,
          line,
          side: "right",
          inReplyTo: 0,
          body,
        }),
      onReply: (inReplyTo, body) =>
        session.feature("review").publish("addComment", {
          number: pr.number,
          path,
          line: 0,
          side: "right",
          inReplyTo,
          body,
        }),
    };
  };

  const clearPresentedProposal = (): void => {
    const review = activeReview;
    if (review === undefined) {
      return;
    }
    activeReview = undefined;
    setReviewActive(false);
    host?.endReview(review.session, review.path, false, review.original);
    if (review.reviewUri !== undefined) {
      inlineDiff?.clearByUri(review.reviewUri);
    }
  };

  const presentProposal = (session: ClientSession, proposal: SessionProposal): void => {
    const editorHost = host;
    if (editorHost === undefined || selectedSession() !== session) {
      return;
    }
    if (activeReview?.session === session && activeReview.id === proposal.id) {
      setReviewActive(true);
      if (activeReview.reviewUri !== undefined) {
        inlineDiff?.setByUri(activeReview.reviewUri, {
          original: proposal.original,
          claudeVersion: proposal.proposed,
          mode: "review",
          onAccept: () => resolveReview(true),
          onReject: () => resolveReview(false),
        });
      }
      return;
    }

    clearPresentedProposal();
    const reviewUri = editorHost.beginReview(
      session,
      proposal.path,
      proposal.proposed,
      firstChangedLine?.(proposal.original, proposal.proposed) ?? 1,
    );
    activeReview = { session, reviewUri, ...proposal };
    setReviewActive(true);
    inlineDiff?.setByUri(reviewUri, {
      original: proposal.original,
      claudeVersion: proposal.proposed,
      mode: "review",
      onAccept: () => resolveReview(true),
      onReject: () => resolveReview(false),
    });
  };

  const renderTurnDiff = (session: ClientSession, message: ReviewFileDiff): void => {
    const state = reviews.board(session);
    const files = state.files.map((file) => file.summary());
    if (message.acceptedBaseline === message.current) {
      inlineDiff?.clear(session, message.path);
      commentProse?.refresh();
      const active = activePathFor(session);
      if (active !== null && samePath(active, message.path) && files.length > 1) {
        advanceToNextPendingFile(session, message.path);
      }
      return;
    }

    const index = files.findIndex((file) => samePath(file.path, message.path));
    const fileNavigation =
      files.length > 1 && index !== -1
        ? {
            onPrevFile: (): void => {
              stepReviewFile(-1);
            },
            onNextFile: (): void => {
              stepReviewFile(1);
            },
            fileIndex: index + 1,
            fileCount: files.length,
          }
        : {};
    inlineDiff?.set(session, message.path, {
      original: message.baseline,
      acceptedBaseline: message.acceptedBaseline,
      claudeVersion: message.current,
      mode: "applied",
      onKeepHunk: (hunk) => keepHunk(session, message.path, hunk),
      onKeepFile: () => keepFile(session, message.path),
      onRevertHunk: (hunk) => revertHunk(session, message.path, hunk),
      onRevertFile: () => revertFile(session, message.path),
      onUnkeepHunk: (hunk) => unkeepHunk(session, message.path, hunk),
      onKeepAll: () => session.feature("review").publish("accept", {}),
      onUndo: () => revertAllFor(session),
      fileLabel: message.name,
      ...(state.label !== "" ? { reviewLabel: state.label } : {}),
      ...fileNavigation,
      ...prCommentActions(session, message.path),
    });
    commentProse?.refresh();
  };

  const renderReviewState = (session: ClientSession): void => {
    if (selectedSession() !== session) {
      return;
    }
    const state = reviews.board(session);
    const proposal = reviewProposals.get(session) ?? null;
    if (
      activeReview !== undefined &&
      (activeReview.session !== session || proposal === null || activeReview.id !== proposal.id)
    ) {
      clearPresentedProposal();
    }
    resetPresentedReview();
    updateParkedReview(session);
    inlineDiff?.setReviewHistory(state.history);
    for (const file of state.files) {
      const diff = file.diff();
      if (diff !== null) {
        renderTurnDiff(session, diff);
      }
    }
    if (proposal !== null) {
      presentProposal(session, proposal);
    }
  };

  const setReviewFilesFor = (session: ClientSession, files: ReviewFile[], label: string): void => {
    reviews.setFiles(session, files, label);
    renderReviewState(session);
  };

  const setTurnDiffFor = (session: ClientSession, message: ReviewFileDiff): void => {
    reviews.setDiff(session, message);
    if (selectedSession() === session) {
      renderTurnDiff(session, message);
    }
  };

  const setReviewCommentsFor = (session: ClientSession, message: ReviewComments): void => {
    const state = reviews.setComments(session, message);
    if (selectedSession() !== session) {
      return;
    }
    const diff = state.files.find((file) => samePath(file.summary().path, message.path))?.diff();
    if (diff !== null && diff !== undefined) {
      renderTurnDiff(session, diff);
    }
  };

  const resetReviewFor = (session: ClientSession): void => {
    reviews.reset(session);
    reviewProposals.delete(session);
    renderReviewState(session);
  };

  const showProposal = (session: ClientSession, message: DiffProposal): void => {
    const priorActive = activePathFor(session);
    const addedTab = !openTabsFor(session).some((tab) => samePath(tab.path, message.path));
    reviewProposals.set(session, { ...message, priorActive, addedTab });
    openTabFor(session, message.path, {});
    activateDestinationFor(session);
    renderReviewState(session);
  };

  const closeProposal = (session: ClientSession, id: string): void => {
    const proposal = reviewProposals.get(session) ?? null;
    if (proposal === null || proposal.id !== id) {
      return;
    }
    reviewProposals.delete(session);
    if (activeReview?.session === session && activeReview.id === id) {
      clearPresentedProposal();
    }
    if (proposal.addedTab) {
      dropReviewTabFor(session, proposal.path, proposal.priorActive);
    }
    if (selectedSession() === session) {
      deps.onCurrentFileChanged(activePathFor(session));
      renderReviewState(session);
    }
  };

  const replaceProposals = (session: ClientSession, proposals: DiffProposal[]): void => {
    const next = proposals.at(-1);
    const current = reviewProposals.get(session) ?? null;
    if (next === undefined) {
      if (current !== null) {
        closeProposal(session, current.id);
      }
      return;
    }
    if (current?.id === next.id) {
      reviewProposals.set(session, { ...current, ...next });
      renderReviewState(session);
      return;
    }
    if (current !== null) {
      closeProposal(session, current.id);
    }
    showProposal(session, next);
  };

  const handleFileChanges = (
    session: ClientSession,
    changes: { path: string; kind: "updated" | "added" | "deleted" }[],
  ): void => {
    for (const change of changes) {
      if (change.kind !== "deleted") {
        continue;
      }
      reviews.removeFile(session, change.path);
      const entry = openTabsFor(session).find((tab) => samePath(tab.path, change.path));
      if (entry === undefined) {
        continue;
      }
      const wasActive = activePathFor(session) === entry.path;
      const result = closeTabFor(session, entry.path);
      if (result !== null && wasActive) {
        applyOrClear(session, result.next);
      }
      host?.closeFile(session, entry.path, true);
    }
    renderReviewState(session);
  };

  const offSessionFeatures = registerSessionFeature((session) => {
    const editor = session.feature("editor");
    const review = session.feature("review");
    const files = session.feature("files");
    const revise = session.feature("revise");
    const cleanups = [
      editor.handle<Record<string, never>, { session: EditorSession }>("flush", async () => {
        flushEditorSessionFor(session);
        await host?.flushSession(session);
        return { session: editorSessionFor(session) ?? { active: null, open: [] } };
      }),
      editor.on<{
        path: string;
        line: number;
        preview?: boolean;
        scratch?: boolean;
      }>("openFile", (message) => {
        openFileFor(
          session,
          message.path,
          message.line,
          message.preview === true,
          message.scratch === true,
        );
      }),
      editor.on<{ id: string; path: string; title: string; markdown: string }>(
        "agentPlan",
        (message) => {
          setAgentPlan(session, message.path, message.id, message.title, message.markdown);
        },
      ),
      editor.on<{ path: string; kind: "web" | "source" | "plan" }>(
        "openOverlay",
        ({ path, kind }) => {
          const result = openTabFor(session, path, { kind });
          if (activateDestinationFor(session)) {
            void applyActive(session, result).then(focusEditorSurface);
          }
        },
      ),
      editor.on<DiffProposal>("showDiff", (message) => showProposal(session, message)),
      editor.on<{ proposals: DiffProposal[] }>("diffSnapshot", ({ proposals }) =>
        replaceProposals(session, proposals),
      ),
      editor.on<{ id: string }>("closeDiff", ({ id }) => closeProposal(session, id)),
      editor.on<{ path: string }>("closeTab", ({ path }) => closeTabForSession(session, path)),
      review.on<{ label: string; files: ReviewFile[] }>("changes", ({ label, files }) =>
        setReviewFilesFor(session, files, label),
      ),
      review.on<ReviewFileDiff>("diff", (message) => setTurnDiffFor(session, message)),
      review.on<ReviewComments>("comments", (message) => setReviewCommentsFor(session, message)),
      review.on("reset", () => resetReviewFor(session)),
      revise.on<{ regions: ReviseRegion[] }>("state", ({ regions }) =>
        reviseMarks?.set(session, regions),
      ),
      // The host asks before it writes: only this page knows whether the buffer is dirty or the region moved.
      revise.handle<{ id: number }, { ok: boolean; reason: string }>("confirm", ({ id }) => {
        const refusal = reviseMarks?.verify(session, id) ?? null;
        return { ok: refusal === null, reason: refusal ?? "" };
      }),
      review.on<ReviewHistory>("history", (history) => {
        const state = reviews.setHistory(session, history);
        if (selectedSession() === session) {
          inlineDiff?.setReviewHistory(state.history);
        }
      }),
      files.on<{
        changes: { path: string; kind: "updated" | "added" | "deleted" }[];
      }>("changed", ({ changes }) => handleFileChanges(session, changes)),
      session.state.editor.subscribe((restored) => {
        if (restored !== null && editorMounted && selectedSession() === session) {
          void rebindSession(session).catch((error: unknown) => {
            log("error", `editor session restore failed: ${String(error)}`);
            deps.onOpenError(`Couldn't restore the editor session: ${String(error)}`);
          });
        }
      }),
    ];
    return () => {
      for (const cleanup of cleanups) {
        cleanup();
      }
    };
  });

  const offSelection = onSelectedSession((session) => {
    if (!editorMounted) {
      reviews.select(session);
      return;
    }
    reviews.select(session);
    if (session === null) {
      clearPresentedProposal();
      resetPresentedReview();
      updateParkedReview(null);
      host?.clear();
      deps.onCurrentFileChanged(null);
      return;
    }
    void rebindSession(session).catch((error: unknown) => {
      log("error", `editor session rebind failed: ${String(error)}`);
      deps.onOpenError(`Couldn't switch editor sessions: ${String(error)}`);
    });
  });

  interface ScratchSaveResult {
    scratchPath: string;
    savedPath: string;
  }

  const applyScratchSave = (session: ClientSession, result: ScratchSaveResult): void => {
    if (result.savedPath === "") {
      return;
    }
    const activation = convertScratchFor(session, result.scratchPath, result.savedPath);
    if (activation !== null) {
      void applyActive(session, activation);
    }
    host?.closeFile(session, result.scratchPath, true);
  };

  // Ask the host to create a scratch buffer; it comes back as an open-file with `scratch: true`.
  const newFile = (): void => {
    selectedSession()?.feature("editor").publish("newScratch", {});
  };

  // Save the active editor. A scratch buffer is sent to the host for a save-as dialog (autosave cancelled first
  // so nothing re-creates the temp); a real file is already autosaved. Returns true either way.
  const save = (): boolean => {
    const session = selectedSession();
    const path = activePath();
    if (session === null || path === null) {
      return true;
    }
    const entry = openTabsFor(session).find((tab) => tab.path === path);
    if (entry?.scratch === true) {
      // Only the native shell bound to its own local backend has a native Save-As dialog (save-scratch-as);
      // otherwise prompt in-app for a name and send it for the host to resolve under the workspace.
      if (isBrowserHostedShell() || !session.connection.isLocal) {
        void deps.promptScratchName(basename(path)).then((name) => {
          if (name === null) {
            return;
          }
          host?.cancelSave(session, path);
          void session
            .feature("editor")
            .request<ScratchSaveResult, { path: string; content: string; name: string }>(
              "saveScratchNamed",
              {
                path,
                content: host?.contentOf(session, path) ?? "",
                name,
              },
            )
            .then((result) => applyScratchSave(session, result))
            .catch((error: unknown) => session.connection.reportError(error));
        });
      } else {
        host?.cancelSave(session, path);
        void session
          .feature("editor")
          .request<ScratchSaveResult, { path: string; content: string; suggestedName: string }>(
            "saveScratchAs",
            {
              path,
              content: host?.contentOf(session, path) ?? "",
              suggestedName: basename(path),
            },
          )
          .then((result) => applyScratchSave(session, result))
          .catch((error: unknown) => session.connection.reportError(error));
      }
    }
    return true;
  };

  return {
    start,
    openFile,
    openWebTab,
    openSourceTab,
    focusEditor: focusEditorSurface,
    reviseSelection: () => {
      const session = selectedSession();
      const model = host?.editor.getModel();
      const selection = host?.editor.getSelection();
      if (
        session === null ||
        model == null ||
        selection == null ||
        model.uri.scheme !== SESSION_FILE_SCHEME
      ) {
        return;
      }
      const startLine = selection.startLineNumber;
      // A selection ending at column 1 stops before that line, so the line isn't part of the region.
      const endLine =
        selection.endColumn === 1 && selection.endLineNumber > startLine
          ? selection.endLineNumber - 1
          : selection.endLineNumber;
      // Line content joined with \n: the host splices line ranges and compares its guard the same way, so a
      // CRLF file must not send the model's \r\n back.
      const originalText = model
        .getLinesContent()
        .slice(startLine - 1, endLine)
        .join("\n");
      const path = sessionUriHostPath(model.uri);
      void deps.promptRevision(endLine - startLine + 1).then((instruction) => {
        if (instruction === null) {
          return;
        }
        // Flush the pending save first, so the host's guard reads the same content the editor shows.
        afterFlush(session, path, () =>
          session.feature("revise").publish("start", {
            path,
            startLine,
            endLineExclusive: endLine + 1,
            originalText,
            instruction,
          }),
        );
      });
    },
    openMatch: (path, line, column, focus) => {
      const session = selectedSession();
      if (session !== null) {
        if (focus) {
          activateDestinationFor(session);
        } else {
          reviews.leaveUnified(session);
        }
        void applyActive(
          session,
          openTabFor(session, path, { line, column, focus, preview: true }),
        );
      }
    },
    triggerAction: (actionId) => {
      if (host === undefined) {
        return false;
      }
      const target = host.focusedEditor();
      target.focus();
      target.trigger("weavie-menu", actionId, null);
      return true;
    },
    newFile,
    save,
    flushDirty: () => host?.flushDirty() ?? Promise.resolve(),
    flushSession: (session) => host?.flushSession(session) ?? Promise.resolve(),
    openReview: (session, path, line) => {
      if (selectedSession() !== session) {
        return false;
      }
      if (path === undefined) {
        return showUnifiedReview(session);
      }
      const view = reviews
        .board(session)
        .files.find((candidate) => samePath(candidate.summary().path, path));
      if (view === undefined) {
        return false;
      }
      const file = view.summary();
      openReviewFile(session, file, line ?? file.line);
      return true;
    },
    showBlameAtCursor: () => gitBlame?.showAtCursor() ?? false,
    activeContent,
    reviewActive,
    parkedReviewCount: reviews.count,
    review: {
      mode: reviews.mode,
      overview: reviews.overview,
      openCopy: (session, path) =>
        host === undefined
          ? Promise.reject(new Error("the editor is still loading"))
          : host.openReviewCopy(session, path),
      releaseCopies: () => host?.releaseReviewCopies(),
      toggleMode: (session) => {
        if (selectedSession() !== session) {
          return false;
        }
        const state = reviews.board(session);
        if (state.files.length === 0) {
          return false;
        }
        if (state.mode === "file") {
          return showUnifiedReview(session);
        }
        const cursor = state.cursor;
        const cursorView = state.files.find(
          (candidate) => cursor !== null && samePath(candidate.summary().path, cursor.path),
        );
        const view = cursorView ?? state.files[0];
        if (view === undefined) {
          return false;
        }
        const file = view.summary();
        openReviewFile(
          session,
          file,
          cursorView === undefined ? file.line : (cursor?.line ?? file.line),
        );
        return true;
      },
      setCursor: (session, path, line) => {
        reviews.setCursor(session, { path, line });
      },
      revert: tryRevertAll,
      keepFile: (session, path) => {
        const file = pendingReviewFile(session, path);
        if (file === null) {
          return false;
        }
        keepFile(session, file.path);
        return true;
      },
      revertFile: (session, path) => {
        const file = pendingReviewFile(session, path);
        if (file === null) {
          return false;
        }
        revertFile(session, file.path);
        return true;
      },
      keepAll: (session) => {
        if (reviews.board(session).files.length === 0) {
          return false;
        }
        session.feature("review").publish("accept", {});
        return true;
      },
      revertAll: (session) => {
        return tryRevertAll(session);
      },
      undoKeep: (session) => undoReview(session, "keep"),
      undoRevert: (session) => undoReview(session, "revert"),
      redo: redoReview,
    },
    inline: {
      nextChange: () => inlineDiff?.nextChange() ?? false,
      prevChange: () => inlineDiff?.prevChange() ?? false,
      nextFile: () => inlineDiff?.nextFile() ?? false,
      prevFile: () => inlineDiff?.prevFile() ?? false,
      accept: () => inlineDiff?.accept() ?? false,
      reject: () => inlineDiff?.reject() ?? false,
      undo: () => inlineDiff?.undo() ?? false,
      keepFile: () => inlineDiff?.keepFile() ?? false,
      revertFile: () => inlineDiff?.revertFile() ?? false,
      keepAll: () => inlineDiff?.keepAll() ?? false,
      comment: () => inlineDiff?.comment() ?? false,
      undoKeep: () => inlineDiff?.undoKeep() ?? false,
      undoRevert: () => inlineDiff?.undoRevert() ?? false,
      redoReview: () => inlineDiff?.redoReview() ?? false,
    },
    tabs,
    nav: {
      back: () => {
        const session = selectedSession();
        const acted = session !== null && navHistoryFor(session).back();
        setNavRevision((revision) => revision + 1);
        return acted;
      },
      forward: () => {
        const session = selectedSession();
        const acted = session !== null && navHistoryFor(session).forward();
        setNavRevision((revision) => revision + 1);
        return acted;
      },
      canBack: () => {
        navRevision();
        const session = selectedSession();
        return session !== null && navHistoryFor(session).canBack();
      },
      canForward: () => {
        navRevision();
        const session = selectedSession();
        return session !== null && navHistoryFor(session).canForward();
      },
    },
    symbols,
    dispose: () => {
      window.clearTimeout(initTimer);
      if (navTimer !== undefined) {
        clearTimeout(navTimer);
      }
      for (const sub of contentSubs) {
        sub.dispose();
      }
      commentProse?.dispose();
      gitBlame?.dispose();
      reviseMarks?.dispose();
      inlineDiff?.dispose();
      host?.dispose();
      offSelection();
      offSessionFeatures();
    },
  };
}
