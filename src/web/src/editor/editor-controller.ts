// Owns the Monaco editor lifecycle and diff/review orchestration on App's behalf. Drives the editor host +
// inline-diff layer (editor-host.ts / inline-diff.ts).

import type * as monaco from "monaco-editor";
import { createSignal } from "solid-js";
import {
  activeBackendId,
  currentEditorBinding,
  type EditorBinding,
  isBrowserHostedShell,
  LOCAL_BACKEND_ID,
  log,
  postToBackend,
  postToEditorBinding,
  postToHost,
  type ReviewCommentInfo,
  releaseEditorBinding,
  type WebBoundMessage,
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
import { normalizePath, samePath, uriHostPath } from "./fs-path";
import type { HunkRevert, HunkUnkeep, InlineDiff, InlineDiffOptions } from "./inline-diff";
import { mediaTypeOf } from "./media/media-types";
import { createNavHistory, type NavHistory } from "./nav-history";
import { setAgentPlan } from "./plan/plan-store";
import {
  type ActivateResult,
  activateTab,
  activePath,
  closeMany,
  closeTab,
  convertScratch,
  dropReviewTab,
  openTab,
  openTabs,
  promote,
  togglePin,
} from "./session-store";
import type { EditorSessionEntry } from "./session-types";
import type {
  SpellAddWordResult,
  SpellContext,
  SpellDiagnostics,
  SpellDictionaryChanged,
  SpellMenuTarget,
  SpellScope,
  SpellSuggestResult,
} from "./spelling/spell-session";

// Only a genuine hang trips this, never a slow cold start: the editor chunk (~750KB of Monaco + workers) plus
// vscode-services init can legitimately run tens of seconds on a loaded machine or across the remote worker hop
// (browser → Runner → worker). 15s misfired there — a slow-but-successful boot got killed and `data-ready` never
// stamped — so it's set well above any real cold start while still bounding an init that truly never settles.
const EDITOR_INIT_MS = 60_000;

type SpellMessage =
  | SpellAddWordResult
  | SpellDiagnostics
  | SpellDictionaryChanged
  | SpellSuggestResult;

export interface EditorControllerDeps {
  /** Surface a debounced save that failed to reach disk. */
  onSaveError: (message: string) => void;
  /** Surface a file that couldn't be opened (read), so a failed open errors loudly instead of a blank tab. */
  onOpenError: (message: string) => void;
  /** Report the file the editor is showing so the browser / title bar can track it. */
  onCurrentFileChanged: (path: string | null) => void;
  /** Activate the editor pane and focus its visible overlay; false when Monaco is the visible surface. */
  focusVisibleOverlay: () => boolean;
  /** Confirm discarding unsaved scratch buffers about to be closed (`names`); the single close-path guard. */
  confirmDiscard: (names: string[]) => Promise<boolean>;
  /** Confirm a destructive review action (Revert file / Revert all). Resolves true to proceed. */
  confirm: (options: { title: string; body: string; confirmLabel: string }) => Promise<boolean>;
  /** Prompt in-app for a scratch buffer's save name on a browser-served host (no native Save-As dialog);
   * resolves the chosen workspace-relative name, or null if cancelled. */
  promptScratchName: (suggestedName: string) => Promise<string | null>;
}

/** One changed file in the post-turn review set: path, line counts, and the 1-based line of its first change. */
export interface ReviewFile {
  path: string;
  name: string;
  added: number;
  removed: number;
  line: number;
}

/** Diff nav + actions, exposed so commands (keybindings / palette / Claude) drive the active diff. */
export interface InlineDiffActions {
  nextChange(): boolean;
  prevChange(): boolean;
  /** Walk to the next / previous file in the post-turn review set (applied mode). */
  nextFile(): boolean;
  prevFile(): boolean;
  accept(): boolean;
  reject(): boolean;
  /** Revert the whole turn (revert all); confirms first. */
  undo(): boolean;
  /** Keep / revert every change in the active file (applied review); revertFile confirms first. */
  keepFile(): boolean;
  revertFile(): boolean;
  /** Keep the whole accumulated review set (applied review). */
  keepAll(): boolean;
  /** Comment on the current line (a PR file under review); false (key falls through) otherwise. */
  comment(): boolean;
  /** Undo the most recent keep / revert, or redo the last undone action; false (key falls through) when none. */
  undoKeep(): boolean;
  undoRevert(): boolean;
  redoReview(): boolean;
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
}

export interface EditorController {
  /** Loads the editor chunk and brings up the editor in `container`; fades the splash when settled. */
  start(container: HTMLElement): void;
  /** Opens a file (preview tab when `preview`), replaying once the editor chunk has loaded (last wins). */
  openFile(path: string, line: number, preview?: boolean): void;
  /** Opens an http(s) URL as a web (iframe) tab in the editor tab strip. */
  openWebTab(url: string): void;
  /** Opens a fetched source doc (Notion) as a source (shadow-root) tab in the editor tab strip, keyed by its target. */
  openSourceTab(target: string): void;
  /** Opens host-owned Markdown as a transient, read-only plan tab. */
  openPlanTab(id: string, title: string, markdown: string): void;
  /** Handles an editor-related host message; returns false for messages this controller doesn't own. */
  handleMessage(message: WebBoundMessage): boolean;
  /** Focuses the editor (for focus-pane). */
  focusEditor(): void;
  /** The editor's current selection text for seeding a search: non-empty and single-line, else null. */
  selectionText(): string | null;
  /**
   * Opens a find-in-files hit in the preview tab, landing the cursor at line:column. `focus: false` reveals
   * without stealing focus — the panel's live preview while arrowing through results.
   */
  openMatch(path: string, line: number, column: number, focus: boolean): void;
  /** Focuses the editor and triggers a Monaco action by id (e.g. the editor right-click Copy/Cut/Paste);
   * false when no editor is mounted. */
  triggerAction(actionId: string): boolean;
  /** Resolves a spelling issue under a right-click coordinate, or null outside one. */
  spellContextAtClientPoint(clientX: number, clientY: number): SpellContext | null;
  /** Resolves the spelling issue at the cursor plus a viewport anchor for keyboard-invoked actions. */
  spellContextAtCursor(): SpellMenuTarget | null;
  /** Whether a spelling issue still belongs to the active editor model and diagnostics. */
  isSpellContextCurrent(context: SpellContext): boolean;
  /** Lazily asks Core for corrections to a current spelling issue. */
  requestSpellSuggestions(context: SpellContext): Promise<string[]>;
  /** Applies a current spelling suggestion described by command args. */
  applySpellSuggestion(args: unknown): boolean;
  /** Adds the current spelling issue's word to one Core-owned dictionary. */
  addSpellWord(scope: SpellScope, args: unknown): Promise<void>;
  /** New File: asks the host to create a scratch buffer, which comes back as an open-file with `scratch`. */
  newFile(): void;
  /** Save the active editor: a scratch buffer prompts for a name; a real file is already autosaved. */
  save(): boolean;
  /**
   * Flushes every dirty working copy to the active backend and resolves once they land — called before a
   * cross-backend session switch so edits persist on their own host. Resolves immediately when unmounted.
   */
  flushDirty(): Promise<void>;
  /**
   * Update the review set driving the inline toolbar's ← / → file walk; empty when nothing to review. `label`
   * names a PR/ref review ("PR #12", "vs main") in the subtitle, or is empty for a plain post-turn review.
   */
  setReviewFiles(files: ReviewFile[], label: string): void;
  /** Open the first file in the review set landed on its first change (the manual "jump into review"). */
  openFirstReviewFile(): boolean;
  /** The active file's current working-copy text (reactive), for the Preview overlay; "" when none. */
  activeContent(): string;
  /** Whether an inline openDiff review is showing (reactive), so Preview suspends rather than hiding it. */
  reviewActive(): boolean;
  /** How many files are pending post-turn review (reactive), so the empty-state pane can surface a review cue
   * when no file is open. */
  parkedReviewCount(): number;
  /** Added/removed lines across the complete Review diff, including faded kept changes. */
  reviewLineCounts(): { added: number; removed: number };
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
  const handleSpellMessage = (message: SpellMessage): void => {
    const spelling = host?.spelling;
    if (spelling === undefined) {
      return;
    }
    switch (message.type) {
      case "spell-diagnostics":
        spelling.handleDiagnostics(message);
        break;
      case "spell-suggest-result":
        spelling.handleSuggestResult(message);
        break;
      case "spell-add-word-result":
        spelling.handleAddWordResult(message);
        break;
      case "spell-dictionary-changed":
        spelling.handleDictionaryChanged(message);
        break;
    }
  };
  let inlineDiff: InlineDiff | undefined;
  let commentProse: CommentProse | undefined;
  // Captured from the dynamic inline-diff import in start(); used by the show-diff handler, which can
  // only fire once the editor host (and thus this import) is up.
  let firstChangedLine: ((original: string, modified: string) => number) | undefined;
  let initTimer: number | undefined;
  // Disposables for the content/model listeners that feed activeContent (the live Preview text).
  let contentSubs: { dispose(): void }[] = [];
  let projectionReady = false;
  let projectionRebind = Promise.resolve();
  let acknowledgedBinding: EditorBinding | null = null;
  const acknowledgeProjection = (binding: EditorBinding): void => {
    if (binding.protocol === "legacy" || acknowledgedBinding === binding) {
      return;
    }
    acknowledgedBinding = binding;
    postToEditorBinding(binding, {
      type: "editor-projection-mounted",
      sessionId: binding.sessionId,
      projectionEpoch: binding.projectionEpoch,
      projectionRevision: binding.projectionRevision,
      projectionPageId: binding.projectionPageId,
    });
  };
  const rebindProjection = (binding: EditorBinding, alreadyRebound: boolean): Promise<void> => {
    const run = async (): Promise<void> => {
      const editorHost = host;
      if (editorHost === undefined || currentEditorBinding() !== binding) {
        return;
      }
      if (!alreadyRebound) {
        await editorHost.rebindSession();
      }
      if (currentEditorBinding() !== binding) {
        return;
      }
      deps.onCurrentFileChanged(activePath());
      acknowledgeProjection(binding);
    };
    const queued = projectionRebind.then(run, run);
    projectionRebind = queued.catch(() => undefined);
    return queued;
  };
  // The active file's working-copy text, kept live off the editor model so Preview renders edits/reloads.
  const [activeContent, setActiveContent] = createSignal("");
  // Whether an inline openDiff review currently occupies the editor, so the Preview overlay suspends over it.
  const [reviewActive, setReviewActive] = createSignal(false);
  // How many files are pending post-turn review, so the empty-state pane can surface a "review changes" cue
  // when no file is open (the inline parked toolbar can't render without a mounted editor). See #125.
  const [parkedReviewCount, setParkedReviewCount] = createSignal(0);
  const [reviewLineCounts, setReviewLineCounts] = createSignal({ added: 0, removed: 0 });
  // An open-file request that arrived before the editor was ready; replayed when it is.
  let pendingOpen:
    | {
        binding: EditorBinding | null;
        path: string;
        line: number;
        preview?: boolean;
        scratch?: boolean;
      }
    | undefined;
  // Files Claude changed since the last review, in document order; drives the toolbar's ← / → file walk.
  let reviewFiles: ReviewFile[] = [];
  // Names the review in the toolbar/parked subtitle ("PR #12", "vs main"); empty for a plain post-turn review.
  // Carried on the turn-changes push (from the host's active DiffReview), so the applied surface always names
  // what it's diffing against.
  let reviewLabel = "";
  // A PR file's review comments (+ the PR number to post replies against), keyed by normalized path. Merged into
  // the applied diff so Comment/Reply coexist with Accept/Reject; a present entry (even empty) marks a PR file.
  // Cleared on turn-reset / switch, then repopulated by the host's review-comments pushes.
  const commentsByPath = new Map<string, { number: number; comments: ReviewCommentInfo[] }>();
  // The openDiff under inline review (at most one live, since openDiff blocks). `reviewUri` keys the transient
  // review model the inline diff is rendered over.
  let activeReview:
    | {
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
  const applyActive = (result: ActivateResult): Promise<void> => {
    // An overlay tab has no Monaco model: leave the editor host untouched (App overlays it) and never read the
    // path as a file. Same for a media (image/video) file tab —
    // reading it as a working copy would decode binary as UTF-8 and autosave could write the mojibake back.
    const activeKind = openTabs().find((tab) => tab.path === result.path)?.kind;
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
    return (host?.show(result.path, result.placement) ?? Promise.resolve(false)).then((ok) => {
      if (!ok) {
        rollbackFailedOpen(result.path);
      }
    });
  };

  // Drop a tab whose open failed (no working copy to release) and, if it was active, switch to its neighbor. A
  // cascade is fine: an unreadable neighbor rolls back in turn until a readable tab or empty pane is reached.
  const rollbackFailedOpen = (path: string): void => {
    const wasActive = activePath();
    const result = closeTab(path);
    if (result === null) {
      return;
    }
    if (path === wasActive) {
      applyOrClear(result.next);
    }
  };

  const focusEditorSurface = (): void => {
    if (!deps.focusVisibleOverlay()) {
      host?.editor.focus();
    }
  };

  const openFile = (path: string, line: number, preview = false, scratch = false): void => {
    if (host === undefined) {
      deps.onCurrentFileChanged(path); // optimistic; the editor chunk isn't up yet
      pendingOpen = { binding: currentEditorBinding(), path, line, preview, scratch };
      return;
    }
    void applyActive(openTab(path, { line, preview, scratch })).then(focusEditorSurface);
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
    host.editor.revealRangeInCenterIfOutsideViewport(sym.range);
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
    if (host === undefined) {
      return;
    }
    if (isActiveFile(sym.path)) {
      host.editor.setSelection(sym.range);
      host.editor.revealRangeInCenterIfOutsideViewport(sym.range);
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
  const navHistory: NavHistory = createNavHistory((loc) =>
    host === undefined ? Promise.resolve() : applyActive(openTab(loc.path, { line: loc.line })),
  );

  // Record where the editor settles (active file + cursor line) as a navigation point, debounced like the
  // view-state snapshot so only the resting position is logged — not the brief top-of-file the editor sits at
  // mid-swap before a reveal. Only real file models: overlay (web/source) tabs and the transient review model
  // aren't navigable locations.
  let navTimer: ReturnType<typeof setTimeout> | undefined;
  const recordNavLocation = (): void => {
    const model = host?.editor.getModel();
    const position = host?.editor.getPosition();
    if (model == null || position == null || model.uri.scheme !== "file") {
      return;
    }
    // uriHostPath, not fsPath: a back-navigation re-opens this path as a tab, which must stay host-native.
    navHistory.record({ path: uriHostPath(model.uri), line: position.lineNumber });
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
    void applyActive(openTab(url, { kind: "web" }));
  };

  // Open a fetched source doc (Notion) as a source tab, keyed by its target. No Monaco model — App overlays the
  // SourceView shadow-root render over the editor host when this tab is active; SourceView reads the html by target.
  const openSourceTab = (target: string): void => {
    void applyActive(openTab(target, { kind: "source" }));
  };

  const openPlanTab = (id: string, title: string, markdown: string): void => {
    void applyActive(openTab(setAgentPlan(id, title, markdown), { kind: "plan" })).then(
      focusEditorSurface,
    );
  };

  // Switch the editor off a closing tab before its working copy is released, else clear to an empty pane.
  const applyOrClear = (next: ActivateResult | null): void => {
    if (next !== null) {
      void applyActive(next);
    } else {
      host?.clear();
      deps.onCurrentFileChanged(null);
    }
  };

  const basename = (path: string): string => path.split(/[\\/]/).pop() ?? path;

  // True if `path` is a scratch (untitled) buffer holding real content — the only tab whose close can lose
  // unsaved work, since real files autosave.
  const isDirtyScratch = (path: string): boolean => {
    const entry = openTabs().find((tab) => tab.path === path);
    if (entry?.scratch !== true) {
      return false;
    }
    return (host?.contentOf(path) ?? "").trim().length > 0;
  };

  // The one guard every close path runs through: if any doomed tab is an unsaved scratch, confirm once before
  // closing. Resolves true to proceed, false to abort. Empty scratches need no confirm.
  const guardDiscard = async (doomed: string[]): Promise<boolean> => {
    const dirty = doomed.filter(isDirtyScratch);
    if (dirty.length === 0) {
      return true;
    }
    return deps.confirmDiscard(dirty.map(basename));
  };

  // Release a closed tab's working copy. A scratch tab is discarded — its model is dropped without flushing
  // and the host deletes its temp file; a real file flushes its pending save first.
  const releaseClosed = (path: string, scratch: boolean): void => {
    if (scratch) {
      host?.closeFile(path, true);
      postToHost({ type: "discard-scratch", path });
    } else {
      host?.closeFile(path);
    }
  };

  // Close every tab matching `predicate` (closeMany skips pinned). Guards unsaved scratch work first (one
  // confirm for the batch), switches off a doomed active tab, then releases each closed working copy.
  const closeBy = async (predicate: (entry: EditorSessionEntry) => boolean): Promise<void> => {
    const doomed = openTabs().filter((entry) => predicate(entry) && entry.pinned !== true);
    if (doomed.length === 0 || !(await guardDiscard(doomed.map((entry) => entry.path)))) {
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
    const wasActive = activePath();
    const result = closeMany(predicate);
    if (result.disposed.length === 0) {
      return;
    }
    if (wasActive !== null && result.disposed.includes(wasActive)) {
      applyOrClear(result.next);
    }
    for (const path of result.disposed) {
      if (!overlayPaths.has(path)) {
        releaseClosed(path, scratchPaths.has(path));
      }
    }
    for (const entry of doomed) {
      if (result.disposed.includes(entry.path)) {
        recordClosed(entry);
      }
    }
  };

  // Recently-closed file/web/source tabs, most-recent last, so Reopen Closed Editor (Ctrl+Shift+T) can bring one
  // back. Scratch and virtual plan tabs are excluded because neither is a persistent document.
  const closedTabs: { path: string; kind: EditorSessionEntry["kind"] }[] = [];
  const CLOSED_TABS_LIMIT = 25;
  const recordClosed = (entry: EditorSessionEntry): void => {
    if (entry.scratch === true || entry.kind === "plan") {
      return;
    }
    closedTabs.push({ path: entry.path, kind: entry.kind });
    if (closedTabs.length > CLOSED_TABS_LIMIT) {
      closedTabs.shift();
    }
  };
  // Reopen the most recently closed tab that isn't already open again; skip stale records for tabs reopened by
  // other means. Declines (returns false) when there's nothing to reopen, so Ctrl+Shift+T falls through.
  const reopenClosed = (): boolean => {
    while (closedTabs.length > 0) {
      const entry = closedTabs.pop();
      if (entry === undefined || openTabs().some((tab) => samePath(tab.path, entry.path))) {
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

  const closeTabAction = async (path: string): Promise<void> => {
    // `path` may arrive from the host (Claude's close_tab) spelled differently than the stored key, so match
    // by normalized identity, then operate on the entry's own stored path downstream.
    const entry = openTabs().find((tab) => samePath(tab.path, path));
    if (entry === undefined || !(await guardDiscard([entry.path]))) {
      return;
    }
    const scratch = entry.scratch === true;
    const wasActive = activePath();
    const result = closeTab(entry.path);
    if (result === null) {
      return;
    }
    recordClosed(entry);
    if (entry.path === wasActive) {
      applyOrClear(result.next);
    }
    // Overlay tabs have no working copy / Monaco model to release.
    if (entry.kind !== "web" && entry.kind !== "source" && entry.kind !== "plan") {
      releaseClosed(result.disposed, scratch);
    }
  };

  // Step through tabs in visual order, wrapping. Returns false (so the keybinding falls through to the editor)
  // when there's nothing to step to.
  const step = (delta: number): boolean => {
    const list = openTabs();
    if (list.length < 2) {
      return false;
    }
    const idx = list.findIndex((tab) => tab.path === activePath());
    if (idx === -1) {
      return false;
    }
    const target = list[(idx + delta + list.length) % list.length];
    if (target === undefined) {
      return false;
    }
    const result = activateTab(target.path);
    if (result !== null) {
      void applyActive(result);
    }
    return true;
  };

  const closeRelative = (path: string, side: "left" | "right"): void => {
    const list = openTabs();
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
      const result = activateTab(path);
      if (result !== null) {
        void applyActive(result);
      }
    },
    close: (path) => {
      const subject = target(path);
      if (subject !== null) {
        void closeTabAction(subject);
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
      if (subject !== null) {
        togglePin(subject);
      }
    },
    promote: (path) => {
      const subject = target(path);
      if (subject !== null) {
        promote(subject);
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
    const finalContents = host?.endReview(review.path, keep, review.original) ?? "";
    if (review.reviewUri !== undefined) {
      inlineDiff?.clearByUri(review.reviewUri);
    }
    // A rejected proposal whose tab was opened just to review it: drop it and fall back to the previously
    // active tab (a store-only fixup; endReview already restored the editor). A kept file stays open.
    if (!keep && review.addedTab) {
      dropReviewTab(review.path, review.priorActive);
    }
    deps.onCurrentFileChanged(activePath());
    postToHost({
      type: "diff-resolved",
      id: review.id,
      kept: keep,
      finalContents: keep ? finalContents : "",
    });
  };

  // Brings up the editor, holding the splash until a deterministic outcome — editor ready or a real failure
  // (chunk load, crash, or an init that never settles within EDITOR_INIT_MS) — so the reveal shows a settled UI.
  const start = (container: HTMLElement): void => {
    const startingBinding = currentEditorBinding();
    const editorReady = import("./editor-host").then(({ createEditorHost }) =>
      createEditorHost(container, deps.onSaveError, deps.onOpenError, (loc) =>
        navHistory.record(loc),
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
        const [diff, prose, symbolMod] = await Promise.all([
          import("./inline-diff"),
          import("./comment-prose"),
          import("../symbols/symbol-source"),
        ]);
        symbolSource = symbolMod.createSymbolSource(created.editor);
        firstChangedLine = diff.firstChangedLine;
        inlineDiff = diff.createInlineDiff(created.editor);
        // Review undo/redo is session-global (not tied to a file), so its post-callbacks are bound once. `kind`
        // targets the type-split chords; the generic Undo (toolbar) omits it.
        inlineDiff.bindHistory({
          onUndoKeep: () => postToHost({ type: "review-undo", kind: "keep" }),
          onUndoRevert: () => postToHost({ type: "review-undo", kind: "revert" }),
          onUndoLast: () => postToHost({ type: "review-undo" }),
          onRedo: () => postToHost({ type: "review-redo" }),
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
        let reboundBinding = startingBinding;
        while (currentEditorBinding() !== null) {
          const binding = currentEditorBinding();
          if (binding === null) {
            break;
          }
          await rebindProjection(binding, binding === reboundBinding);
          if (currentEditorBinding() === binding) {
            break;
          }
          reboundBinding = null;
        }
        if (pendingOpen !== undefined) {
          const { binding, path, line, preview, scratch } = pendingOpen;
          pendingOpen = undefined;
          if (binding === null || currentEditorBinding() === binding) {
            openFile(path, line, preview, scratch);
          }
        }
        // Reflect whatever file the editor ended up showing (replayed pending-open or hot-reload restore).
        const model = created.editor.getModel();
        if (model !== null && model.uri.scheme === "file") {
          deps.onCurrentFileChanged(uriHostPath(model.uri));
        }
        // Deterministic "editor is usable" signal: the shell now reveals before the editor chunk settles
        // (App defers start past first paint), so tests and any editor-gated UI wait on this, not on the splash.
        container.setAttribute("data-ready", "true");
        projectionReady = true;
        postToHost({ type: "monaco-ready" });
        mark("editor-ready");
      })
      .catch((error: unknown) => {
        log("error", `editor init failed: ${String(error)}`);
        const binding = currentEditorBinding();
        if (binding !== null) {
          releaseEditorBinding(binding);
        }
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
  const openReviewFile = (file: ReviewFile): void => {
    openFile(file.path, file.line, true);
    // Re-request the diff so applied markers (and any merged PR comments) render even if the push was missed.
    postToHost({ type: "get-turn-diff", path: file.path });
  };

  // Monotonic revision of the published review set.
  let reviewRev = 0;
  // Reflect the review set onto the inline-diff's parked navigator: it surfaces (parked at "change 0", editor
  // untouched) whenever files are pending and none is in view, so review is visible the moment changes land —
  // stepping in (a nav key) opens the first change. Called wherever reviewFiles changes.
  const updateParkedReview = (): void => {
    setParkedReviewCount(reviewFiles.length);
    setReviewLineCounts(
      reviewFiles.reduce(
        (total, file) => ({
          added: total.added + file.added,
          removed: total.removed + file.removed,
        }),
        { added: 0, removed: 0 },
      ),
    );
    // Publish the live review-walk set for e2e / diagnostics (read-only) — a failed PR-switch test attaches
    // exactly which files the navigator holds, so a leaked cross-PR mix is visible without walking it. `rev`
    // is a monotonic counter bumped on every change so a test can detect quiescence exactly (poll-sampling
    // the file list alone can miss a fast bounce during a rapid switch storm's push drain).
    reviewRev += 1;
    window.__WEAVIE_REVIEW__ = {
      files: reviewFiles.map((file) => file.path),
      label: reviewLabel,
      rev: reviewRev,
    };
    inlineDiff?.setParkedReview(
      reviewFiles.length > 0
        ? {
            fileCount: reviewFiles.length,
            ...(reviewLabel !== "" ? { label: reviewLabel } : {}),
            stepIn: () => {
              const first = reviewFiles[0];
              if (first !== undefined) {
                openReviewFile(first);
              }
            },
          }
        : undefined,
    );
  };

  const resetReviewProjection = (): void => {
    inlineDiff?.clearAll();
    inlineDiff?.setReviewHistory({
      canUndo: false,
      canUndoKeep: false,
      canUndoRevert: false,
      canRedo: false,
    });
    reviewFiles = [];
    reviewLabel = "";
    commentsByPath.clear();
    updateParkedReview();
    commentProse?.refresh();
  };

  // Step the file axis of the review walk: open the neighbour (wrapping) at its first change. Returns false
  // (so Ctrl+Left/Right keep Win/Linux word-nav) when there's no multi-file review. An active file that fell
  // OUT of the set (a session switch's in-flight rebind briefly leaves a stale tab on screen) re-enters at the
  // first file — a nav key pressed at a live review toolbar must never silently no-op.
  const stepReviewFile = (delta: number): boolean => {
    if (reviewFiles.length < 2) {
      return false;
    }
    const current = activePath();
    const idx = current === null ? -1 : reviewFiles.findIndex((f) => samePath(f.path, current));
    const next =
      idx === -1
        ? reviewFiles[0]
        : reviewFiles[(idx + delta + reviewFiles.length) % reviewFiles.length];
    if (next === undefined) {
      return false;
    }
    openReviewFile(next);
    return true;
  };

  // A file's diff just cleared (its last hunk was kept or reverted) while other changed files remain under
  // review: open the next changed file (wrapping, on its first change) so the toolbar follows the review
  // instead of vanishing. Only called when more than one file remains; the kept/reverted file is skipped
  // since the host drops it from the review set right after.
  const advanceToNextPendingFile = (fromPath: string): void => {
    const idx = reviewFiles.findIndex((f) => samePath(f.path, fromPath));
    const start = idx === -1 ? 0 : idx;
    for (let step = 1; step <= reviewFiles.length; step++) {
      const candidate = reviewFiles[(start + step) % reviewFiles.length];
      if (candidate !== undefined && !samePath(candidate.path, fromPath)) {
        openReviewFile(candidate);
        return;
      }
    }
  };

  // Flush the file's pending save (so the host reverts from current disk content), then run `send`. Both the
  // per-hunk and whole-file reverts go through this so the host never races a debounced write. A failed flush
  // means the revert would act against stale disk content, so surface it and abort rather than misapply silently.
  const afterFlush = (path: string, send: () => void): void => {
    const flushed = host?.flush(path);
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
  const revertHunk = (path: string, hunk: HunkRevert): void => {
    afterFlush(path, () => postToHost({ type: "reject-hunk", path, ...hunk }));
  };

  // Keep just this hunk: the host advances its review baseline over it (no disk write) so it drops from the
  // pending diff for good. Flush first so the host's guardText check sees the same disk content the web does.
  const keepHunk = (path: string, hunk: HunkRevert): void => {
    afterFlush(path, () => postToHost({ type: "keep-hunk", path, ...hunk }));
  };

  // Un-keep just this faded hunk: the host splices the accepted-anchor lines back into the review baseline, so it
  // returns to the bright pending band. No disk read (the guard is against Core's review baseline), so no flush.
  const unkeepHunk = (path: string, hunk: HunkUnkeep): void => {
    postToHost({ type: "unkeep-hunk", path, ...hunk });
  };

  // Keep every change in one file: the host advances its review baseline to current, so the file leaves the
  // review set for good. No confirm — keeping is non-destructive.
  const keepFile = (path: string): void => {
    afterFlush(path, () => postToHost({ type: "keep-file", path }));
  };

  // Revert every change in one file to its turn baseline on disk, after a confirm (the host restores the file
  // wholesale and re-emits its now-empty diff + the trimmed review set).
  const revertFile = (path: string): void => {
    void deps
      .confirm({
        title: "Revert file?",
        body: `Discard all changes to "${basename(path)}" and restore it to before this turn? You can undo this afterward.`,
        confirmLabel: "Revert file",
      })
      .then((ok) => {
        if (ok) {
          afterFlush(path, () => postToHost({ type: "revert-file", path }));
        }
      });
  };

  // Revert the whole turn (revert all), after a confirm — the host reverts every touched file to its baseline.
  const revertAll = (): void => {
    const count = reviewFiles.length;
    void deps
      .confirm({
        title: "Revert all changes?",
        body: `Discard every change from this turn${count > 1 ? ` across ${count} files` : ""}? You can undo this afterward.`,
        confirmLabel: "Revert all",
      })
      .then((ok) => {
        if (ok) {
          postToHost({ type: "undo-turn" });
        }
      });
  };

  // The Comment/Reply actions for a PR file under review (nothing for a plain turn file), merged into the applied
  // diff so commenting coexists with Accept/Reject on the one toolbar. `number` is the PR to post against.
  const prCommentActions = (
    path: string,
  ): Pick<InlineDiffOptions, "comments" | "onAddComment" | "onReply"> => {
    const pr = commentsByPath.get(normalizePath(path));
    if (pr === undefined) {
      return {};
    }
    return {
      comments: pr.comments,
      onAddComment: (line, body) =>
        postToHost({
          type: "add-pr-comment",
          number: pr.number,
          path,
          line,
          side: "right",
          inReplyTo: 0,
          body,
        }),
      onReply: (inReplyTo, body) =>
        postToHost({
          type: "add-pr-comment",
          number: pr.number,
          path,
          line: 0,
          side: "right",
          inReplyTo,
          body,
        }),
    };
  };

  const handleMessage = (message: WebBoundMessage): boolean => {
    switch (message.type) {
      case "show-agent-plan":
        openPlanTab(message.id, message.title, message.markdown);
        return true;
      case "show-diff": {
        // Render Claude's openDiff proposal inline over a transient review model (the real working copy is
        // never touched): the editor shows `proposed`, diffed vs `original`, with a Keep/Reject toolbar.
        const priorActive = activePath();
        const wasOpen = openTabs().some((tab) => samePath(tab.path, message.path));
        // Reveal the proposal at its first changed hunk, not the top of the file.
        const reviewUri = host?.beginReview(
          message.path,
          message.proposed,
          // host is set in the same .then that captures firstChangedLine, so it's defined here; fall
          // back to the file top if somehow not.
          firstChangedLine?.(message.original, message.proposed) ?? 1,
        );
        activeReview = {
          id: message.id,
          path: message.path,
          original: message.original,
          reviewUri,
          addedTab: !wasOpen,
          priorActive,
        };
        setReviewActive(true);
        // Make the reviewed file active so the strip + title name it; activeReview is set first, so
        // applyActive's guard keeps the transient review model showing.
        void applyActive(openTab(message.path));
        if (reviewUri !== undefined) {
          inlineDiff?.setByUri(reviewUri, {
            original: message.original,
            claudeVersion: message.proposed,
            mode: "review",
            onAccept: () => resolveReview(true),
            onReject: () => resolveReview(false),
          });
        }
        return true;
      }
      case "close-diff":
        // Host cancelled the openDiff: tear the review down without replying (the host's awaiting task is
        // already cancelled). Treated like a reject: drop a tab opened just to review, then re-sync title.
        if (activeReview?.id === message.id) {
          const review = activeReview;
          activeReview = undefined;
          setReviewActive(false);
          host?.endReview(review.path, false, review.original);
          if (review.reviewUri !== undefined) {
            inlineDiff?.clearByUri(review.reviewUri);
          }
          if (review.addedTab) {
            dropReviewTab(review.path, review.priorActive);
          }
          deps.onCurrentFileChanged(activePath());
        }
        return true;
      case "set-editor-session":
        // Projection commit owns the complete editor/review surface. Clear the outgoing review immediately,
        // then serialize Monaco rebinds; only the rebind for the still-current immutable binding may mount.
        resetReviewProjection();
        host?.spelling.clearProjection();
        if (projectionReady && host !== undefined) {
          const binding = currentEditorBinding();
          if (binding !== null) {
            void rebindProjection(binding, false).catch((error: unknown) => {
              log("error", `editor projection rebind failed: ${String(error)}`);
              deps.onOpenError(`Couldn't switch editor sessions: ${String(error)}`);
              releaseEditorBinding(binding);
            });
          }
        }
        return true;
      case "spell-diagnostics":
        handleSpellMessage(message);
        return true;
      case "spell-suggest-result":
        handleSpellMessage(message);
        return true;
      case "spell-add-word-result":
        handleSpellMessage(message);
        return true;
      case "spell-dictionary-changed":
        handleSpellMessage(message);
        return true;
      case "open-file":
        openFile(message.path, message.line, message.preview === true, message.scratch === true);
        return true;
      case "scratch-saved": {
        // Scratch saved under a real name: convert the tab to the saved file, or (when saved outside the
        // workspace) drop the scratch tab, then release the scratch model without flushing.
        if (message.savedPath === "") {
          return true; // the user cancelled the save dialog
        }
        if (message.reopen) {
          const result = convertScratch(message.scratchPath, message.savedPath);
          if (result !== null) {
            void applyActive(result);
          }
        } else {
          const wasActive = activePath() === message.scratchPath;
          const result = closeTab(message.scratchPath);
          if (result !== null && wasActive) {
            applyOrClear(result.next);
          }
        }
        host?.closeFile(message.scratchPath, true);
        return true;
      }
      case "close-tab":
        // Claude's close_tab MCP tool: the host resolved the tab name to our path key.
        tabs.close(message.path);
        return true;
      case "turn-diff": {
        // Inline diff of this turn's changes, as the (acceptedBaseline, baseline, current) triple: baseline→current
        // is the bright pending band, acceptedBaseline→baseline the faded accepted band. The file has NO markers
        // only once the accepted anchor catches up to current (keep-all, or a full revert with nothing kept).
        if (message.acceptedBaseline === message.current) {
          inlineDiff?.clear(message.path);
          commentProse?.refresh();
          // A revert emptied this file's diff. If it's under review and other changed files remain, walk on to
          // the next so the toolbar follows the review instead of vanishing.
          const active = activePath();
          if (active !== null && samePath(active, message.path) && reviewFiles.length > 1) {
            advanceToNextPendingFile(message.path);
          }
          return true;
        }
        // The toolbar's ← / → file axis: only for a multi-file review containing this file, so a single-file
        // review leaves ctrl+$mod+Left/Right unclaimed (Win/Linux word-nav).
        const idx = reviewFiles.findIndex((f) => samePath(f.path, message.path));
        const fileNav =
          reviewFiles.length > 1 && idx !== -1
            ? {
                onPrevFile: (): void => {
                  stepReviewFile(-1);
                },
                onNextFile: (): void => {
                  stepReviewFile(1);
                },
                fileIndex: idx + 1,
                fileCount: reviewFiles.length,
              }
            : {};
        inlineDiff?.set(message.path, {
          original: message.baseline,
          acceptedBaseline: message.acceptedBaseline,
          claudeVersion: message.current,
          mode: "applied",
          onKeepHunk: (hunk) => keepHunk(message.path, hunk),
          onKeepFile: () => keepFile(message.path),
          onRevertHunk: (hunk) => revertHunk(message.path, hunk),
          onRevertFile: () => revertFile(message.path),
          onUnkeepHunk: (hunk) => unkeepHunk(message.path, hunk),
          onKeepAll: () => postToHost({ type: "accept-turn" }),
          onUndo: revertAll,
          // Always present so the stacked toolbar label shows the filename even for a single-file review.
          fileLabel: message.name,
          // Names the review ("PR #12", "vs main") in the subtitle; omitted (falsy) for a plain post-turn review.
          ...(reviewLabel !== "" ? { reviewLabel } : {}),
          ...fileNav,
          // A PR file also carries its review comments (Comment/Reply on the same applied toolbar); a plain turn
          // file has no entry, so this spreads nothing.
          ...prCommentActions(message.path),
        });
        // The diff suspends comment-prose over this model; refresh so a collapsed comment re-expands rather
        // than hiding a changed line.
        commentProse?.refresh();
        return true;
      }
      case "review-comments":
        // A PR file's review comments (pushed just before its turn-diff at arm, and again after a post): stash
        // them so the applied set() above merges Comment/Reply in. The host re-emits turn-diff to re-render.
        commentsByPath.set(normalizePath(message.path), {
          number: message.number,
          comments: message.comments,
        });
        return true;
      case "turn-reset":
        // A turn boundary clears the accepted review board; projection switches use the same reducer above.
        resetReviewProjection();
        return true;
      case "review-history":
        // Host-pushed undo/redo availability: drives the toolbar's Undo/Redo buttons and lets the undo chords
        // decline (fall through) when there's nothing of that kind to undo.
        inlineDiff?.setReviewHistory({
          canUndo: message.canUndo,
          canUndoKeep: message.canUndoKeep,
          canUndoRevert: message.canUndoRevert,
          canRedo: message.canRedo,
        });
        return true;
      case "fs-change": {
        // Host-side deletion (e.g. a revert that deleted a created file). Close a deleted file's tab and discard
        // its working copy before the provider fires DELETED, so no "Unable to read file" toast shows. Returns
        // false: the provider still needs to reload updated files.
        for (const change of message.changes) {
          if (change.kind !== "deleted") {
            continue;
          }
          // Drop the deleted file from the ← / → walk, else stepReviewFile lands on an unresolvable path. The
          // host re-pushes corrected turn-changes; pruning here keeps the set consistent in the gap.
          reviewFiles = reviewFiles.filter((file) => !samePath(file.path, change.path));
          updateParkedReview();
          inlineDiff?.clear(change.path);
          const entry = openTabs().find((tab) => samePath(tab.path, change.path));
          if (entry === undefined) {
            continue;
          }
          const wasActive = activePath() === entry.path;
          const result = closeTab(entry.path);
          if (result !== null && wasActive) {
            applyOrClear(result.next);
          }
          host?.closeFile(entry.path, true);
        }
        return false;
      }
      default:
        return false;
    }
  };

  // Ask the host to create a scratch buffer; it comes back as an open-file with `scratch: true`.
  const newFile = (): void => {
    postToHost({ type: "new-scratch" });
  };

  // Save the active editor. A scratch buffer is sent to the host for a save-as dialog (autosave cancelled first
  // so nothing re-creates the temp); a real file is already autosaved. Returns true either way.
  const save = (): boolean => {
    const path = activePath();
    if (path === null) {
      return true;
    }
    const entry = openTabs().find((tab) => tab.path === path);
    if (entry?.scratch === true) {
      // Only the native shell bound to its own local backend has a native Save-As dialog (save-scratch-as);
      // otherwise prompt in-app for a name and send it for the host to resolve under the workspace.
      if (isBrowserHostedShell() || activeBackendId() !== LOCAL_BACKEND_ID) {
        // Pin the backend now: the scratch lives on it, and the user can switch while the prompt is open.
        const backend = activeBackendId();
        void deps.promptScratchName(basename(path)).then((name) => {
          if (name === null) {
            return;
          }
          host?.cancelSave(path);
          postToBackend(backend, {
            type: "save-scratch-named",
            path,
            content: host?.contentOf(path) ?? "",
            name,
          });
        });
      } else {
        host?.cancelSave(path);
        postToHost({
          type: "save-scratch-as",
          path,
          content: host?.contentOf(path) ?? "",
          suggestedName: basename(path),
        });
      }
    }
    return true;
  };

  return {
    start,
    openFile,
    openWebTab,
    openSourceTab,
    openPlanTab,
    handleMessage,
    focusEditor: focusEditorSurface,
    selectionText: () => {
      const selection = host?.editor.getSelection();
      const model = host?.editor.getModel();
      if (
        selection == null ||
        model == null ||
        selection.isEmpty() ||
        selection.startLineNumber !== selection.endLineNumber
      ) {
        return null;
      }
      return model.getValueInRange(selection);
    },
    openMatch: (path, line, column, focus) => {
      void applyActive(openTab(path, { line, column, focus, preview: true }));
    },
    triggerAction: (actionId) => {
      if (host === undefined) {
        return false;
      }
      host.editor.focus();
      host.editor.trigger("weavie-menu", actionId, null);
      return true;
    },
    spellContextAtClientPoint: (clientX, clientY) =>
      host?.spelling.contextAtClientPoint(clientX, clientY) ?? null,
    spellContextAtCursor: () => host?.spelling.contextAtCursor() ?? null,
    isSpellContextCurrent: (context) => host?.spelling.isCurrentContext(context) === true,
    requestSpellSuggestions: (context) =>
      host?.spelling.requestSuggestions(context) ??
      Promise.reject(new Error("The editor isn't ready.")),
    applySpellSuggestion: (args) => host?.spelling.applySuggestion(args) ?? false,
    addSpellWord: (scope, args) =>
      host?.spelling.addWord(scope, args) ??
      Promise.reject(new Error("The spelling editor is not available.")),
    newFile,
    save,
    flushDirty: () => host?.flushDirty() ?? Promise.resolve(),
    setReviewFiles: (files, label) => {
      // The host owns the set: removed files lose their marker, while the remaining set + label drives the
      // parked navigator (including a parked PR/ref review).
      for (const previous of reviewFiles) {
        if (!files.some((file) => samePath(file.path, previous.path))) {
          inlineDiff?.clear(previous.path);
        }
      }
      reviewLabel = label;
      reviewFiles = files;
      updateParkedReview();
    },
    openFirstReviewFile: () => {
      const first = reviewFiles[0];
      if (first === undefined) {
        return false;
      }
      openReviewFile(first);
      return true;
    },
    activeContent,
    reviewActive,
    parkedReviewCount,
    reviewLineCounts,
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
      back: () => navHistory.back(),
      forward: () => navHistory.forward(),
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
      inlineDiff?.dispose();
      host?.dispose();
    },
  };
}
