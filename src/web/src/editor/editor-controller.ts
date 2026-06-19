// Owns the Monaco editor lifecycle and all diff/review orchestration on App's behalf: the deferred
// editor-chunk load (kept off the first-paint path), the openDiff inline-review handshake, and the inline
// diffs for applied turns and session-change browsing. App wires this to host messages and commands; the
// editor host + inline-diff layer it drives live in editor-host.ts / inline-diff.ts.

import { type WebBoundMessage, log, postToHost } from "../bridge";
import { dismissSplash } from "../splash";
import { mark } from "../startup-timing";
import type { EditorHost } from "./editor-host";
import { type InlineDiff, createInlineDiff } from "./inline-diff";
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

// Generous: only a genuine hang trips it, not a slow cold start.
const EDITOR_INIT_MS = 15_000;

export interface EditorControllerDeps {
  /** Surface a debounced save that failed to reach disk (never a silent drop). */
  onSaveError: (message: string) => void;
  /** Report the file the editor is showing so the browser / title bar can track it. */
  onCurrentFileChanged: (path: string | null) => void;
  /**
   * Ask the user to confirm discarding unsaved scratch buffers about to be closed (named by `names`). Resolves
   * true to proceed with the close, false to abort it. The single guard every close path runs through.
   */
  confirmDiscard: (names: string[]) => Promise<boolean>;
}

/** Diff nav + actions, exposed so commands (keybindings / palette / Claude) drive the active diff. */
export interface InlineDiffActions {
  nextChange(): boolean;
  prevChange(): boolean;
  accept(): boolean;
  reject(): boolean;
  undo(): boolean;
}

/**
 * Tab operations, exposed so commands (keybindings / palette / Claude) and the tab strip drive the tab set.
 * The targeted operations default to the active tab when `path` is omitted — the keyboard / palette acts on
 * the active tab, the context menu passes the right-clicked tab.
 */
export interface TabActions {
  /** Switch to an already-open tab, restoring its saved view state. */
  activate(path: string): void;
  /** Close a tab (any state — may close a pinned tab when invoked on it explicitly). Defaults to active. */
  close(path?: string): void;
  /** Close all non-pinned tabs. */
  closeAll(): void;
  /** Close every non-pinned tab except `path` (default active). */
  closeOthers(path?: string): void;
  /** Close non-pinned tabs to the left of `path` (default active). */
  closeToLeft(path?: string): void;
  /** Close non-pinned tabs to the right of `path` (default active). */
  closeToRight(path?: string): void;
  /** Pin or unpin a tab — default active (pinning promotes a preview tab and floats it furthest-left). */
  togglePin(path?: string): void;
  /** Promote a preview tab to persistent (default active). */
  promote(path?: string): void;
  /** Activate the next / previous tab in visual order, wrapping. Returns false if there's nothing to step to. */
  next(): boolean;
  prev(): boolean;
}

export interface EditorController {
  /** Loads the editor chunk and brings up the editor in `container`; fades the splash when settled. */
  start(container: HTMLElement): void;
  /** Opens a file (as a preview tab when `preview`), replaying once the editor chunk has loaded (last wins). */
  openFile(path: string, line: number, preview?: boolean): void;
  /** Handles an editor-related host message; returns false for messages this controller doesn't own. */
  handleMessage(message: WebBoundMessage): boolean;
  /** Focuses the editor (for focus-pane). */
  focusEditor(): void;
  /** New File: asks the host to create a scratch buffer, which comes back as an open-file with `scratch`. */
  newFile(): void;
  /** Save the active editor: a scratch buffer prompts for a name; a real file is already autosaved. */
  save(): boolean;
  readonly inline: InlineDiffActions;
  readonly tabs: TabActions;
  dispose(): void;
}

export function createEditorController(deps: EditorControllerDeps): EditorController {
  // host + inlineDiff are set once the editor chunk loads and the editor is created (see start).
  let host: EditorHost | undefined;
  let inlineDiff: InlineDiff | undefined;
  let initTimer: number | undefined;
  // An open-file request that arrived before the editor was ready; replayed when it is.
  let pendingOpen: { path: string; line: number; preview?: boolean; scratch?: boolean } | undefined;
  // The openDiff under inline review. openDiff blocks per-edit, so at most one is live at a time. `reviewUri`
  // is the transient review model's URI the inline diff is keyed by (review never touches the real file).
  let activeReview:
    | {
        id: string;
        path: string;
        original: string;
        reviewUri: string | undefined;
        // We opened a tab for the reviewed file purely to show the proposal (it wasn't already open). On reject
        // — for a brand-new file, the file was never created — drop that tab again and return to `priorActive`.
        addedTab: boolean;
        // The tab that was active before the review, restored if we drop an `addedTab` on reject/cancel.
        priorActive: string | null;
      }
    | undefined;

  // Show whichever tab a store mutation just made active. The controller is the single translator of "active
  // tab changed" → "swap the editor's model": the tab store owns the set, the host owns Monaco. The working
  // copy resolves its content from disk through the file provider, so no content is passed.
  const applyActive = (result: ActivateResult): void => {
    deps.onCurrentFileChanged(result.path);
    // Don't clobber an in-progress review: the reviewed file is made the active tab (so the strip + title name
    // what's under review), but the editor is showing the TRANSIENT review model keyed by its own URI —
    // re-showing the file's working copy here would drop the diff. The guard lets that tab become/stay active
    // without a model swap; the review model is restored off the editor only by resolveReview → endReview.
    if (activeReview !== undefined && activeReview.path === result.path) {
      return;
    }
    host?.show(result.path, result.placement);
  };

  const openFile = (path: string, line: number, preview = false, scratch = false): void => {
    if (host === undefined) {
      deps.onCurrentFileChanged(path); // optimistic; the editor chunk isn't up yet to show it
      pendingOpen = { path, line, preview, scratch };
      return;
    }
    applyActive(openTab(path, { line, preview, scratch }));
  };

  // Switch the editor off a closing tab before its working copy is released, else clear to an empty pane.
  const applyOrClear = (next: ActivateResult | null): void => {
    if (next !== null) {
      applyActive(next);
    } else {
      host?.clear();
      deps.onCurrentFileChanged(null);
    }
  };

  const basename = (path: string): string => path.split(/[\\/]/).pop() ?? path;

  // True if `path` is a scratch (untitled) buffer holding real content — the only kind of tab whose close can
  // lose unsaved work (real files autosave; editing a preview promotes it, so it's never silently dropped).
  const isDirtyScratch = (path: string): boolean => {
    const entry = openTabs().find((tab) => tab.path === path);
    if (entry?.scratch !== true) {
      return false;
    }
    return (host?.contentOf(path) ?? "").trim().length > 0;
  };

  // The ONE guard every close path runs through: if any doomed tab is an unsaved scratch, confirm once before
  // closing. Resolves true to proceed, false to abort the whole close. Empty scratches need no confirm.
  const guardDiscard = async (doomed: string[]): Promise<boolean> => {
    const dirty = doomed.filter(isDirtyScratch);
    if (dirty.length === 0) {
      return true;
    }
    return deps.confirmDiscard(dirty.map(basename));
  };

  // Release a closed tab's working copy. A scratch tab is DISCARDED — its model is dropped without flushing and
  // the host deletes its temp file; a real file flushes its pending save first.
  const releaseClosed = (path: string, scratch: boolean): void => {
    if (scratch) {
      host?.closeFile(path, true);
      postToHost({ type: "discard-scratch", path });
    } else {
      host?.closeFile(path);
    }
  };

  // Close every tab matching `predicate` (closeMany skips pinned). Guards unsaved scratch work FIRST (one
  // confirm for the batch), then — if the active tab was among them — switches to the surviving neighbor before
  // releasing each closed working copy. Async because the discard confirm is.
  const closeBy = async (predicate: (entry: EditorSessionEntry) => boolean): Promise<void> => {
    const doomed = openTabs().filter((entry) => predicate(entry) && entry.pinned !== true);
    if (doomed.length === 0 || !(await guardDiscard(doomed.map((entry) => entry.path)))) {
      return;
    }
    const scratchPaths = new Set(
      doomed.filter((entry) => entry.scratch === true).map((entry) => entry.path),
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
      releaseClosed(path, scratchPaths.has(path));
    }
  };

  const closeTabAction = async (path: string): Promise<void> => {
    const entry = openTabs().find((tab) => tab.path === path);
    if (entry === undefined || !(await guardDiscard([path]))) {
      return;
    }
    const scratch = entry.scratch === true;
    const wasActive = activePath();
    const result = closeTab(path);
    if (result === null) {
      return;
    }
    if (path === wasActive) {
      applyOrClear(result.next);
    }
    releaseClosed(result.disposed, scratch);
  };

  // Step through tabs in visual order, wrapping. Returns false (declines the keybinding so it falls through to
  // the editor) when there's nothing to step to.
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
      applyActive(result);
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
        applyActive(result);
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
  };

  const resolveReview = (keep: boolean): void => {
    const review = activeReview;
    if (review === undefined) {
      return;
    }
    activeReview = undefined;
    // endReview returns the proposal's final (possibly tweaked) content, which Claude writes to disk on keep,
    // and restores the editor off the transient review model. The review never dirtied the file working copy.
    const finalContents = host?.endReview(review.path, keep, review.original) ?? "";
    if (review.reviewUri !== undefined) {
      inlineDiff?.clearByUri(review.reviewUri);
    }
    // A rejected proposal whose tab we'd opened just to review it (a brand-new file was never created; an
    // existing file we only surfaced): drop that tab and fall back to the previously-active one. endReview has
    // already put the editor back, so this is a store-only fixup. A kept file stays open — for a new file it
    // becomes a real working copy when next opened, after Claude's write lands.
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

  // Brings up the editor off the first-paint path. The splash is held over everything until the editor is
  // ready, then faded once — so the editor's first paint happens *under* the splash and the reveal shows a
  // settled UI. We fade on a DETERMINISTIC outcome only: editor ready, or a real failure (chunk load, editor
  // crash, or an init that never settles within EDITOR_INIT_MS) — which rejects LOUDLY, then frees the splash
  // so the already-working terminals aren't trapped. No silent timer that dismisses while pretending success.
  const start = (container: HTMLElement): void => {
    const editorReady = import("./editor-host").then(({ createEditorHost }) =>
      createEditorHost(container, deps.onSaveError),
    );
    const initDeadline = new Promise<never>((_, reject) => {
      initTimer = window.setTimeout(
        () => reject(new Error(`editor init did not settle within ${EDITOR_INIT_MS}ms`)),
        EDITOR_INIT_MS,
      );
    });
    void Promise.race([editorReady, initDeadline])
      .then((created) => {
        host = created;
        inlineDiff = createInlineDiff(created.editor);
        if (pendingOpen !== undefined) {
          const { path, line, preview, scratch } = pendingOpen;
          pendingOpen = undefined;
          openFile(path, line, preview, scratch);
        }
        // Reflect whatever file the editor ended up showing — a replayed pending-open, or a hot-reload
        // restore of the previously-open file — so the browser / title bar track it.
        const model = created.editor.getModel();
        if (model !== null && model.uri.scheme === "file") {
          deps.onCurrentFileChanged(model.uri.fsPath);
        }
        postToHost({ type: "monaco-ready" });
        mark("editor-ready");
      })
      .catch((error: unknown) => log("error", `editor init failed: ${String(error)}`))
      .finally(() => {
        window.clearTimeout(initTimer);
        dismissSplash();
      });
  };

  const handleMessage = (message: WebBoundMessage): boolean => {
    switch (message.type) {
      case "show-diff": {
        // Render Claude's openDiff proposal INLINE over a TRANSIENT review model (the real file working copy
        // is never touched): the editor shows `proposed`, diffed vs `original`, with a Keep/Reject toolbar.
        const priorActive = activePath();
        const wasOpen = openTabs().some((tab) => tab.path === message.path);
        const reviewUri = host?.beginReview(message.path, message.proposed, 1);
        activeReview = {
          id: message.id,
          path: message.path,
          original: message.original,
          reviewUri,
          addedTab: !wasOpen,
          priorActive,
        };
        // Make the reviewed file the active tab so the tab strip + title name what's under review, rather than
        // leaving the previously-open file selected. activeReview is set first, so applyActive's guard keeps the
        // transient review model showing instead of swapping in the file's working copy.
        applyActive(openTab(message.path));
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
        // Host cancelled the openDiff: tear the review down without replying — the host's awaiting task is
        // already cancelled. Treated like a reject: drop a tab we'd opened just to review, then re-sync title.
        if (activeReview?.id === message.id) {
          const review = activeReview;
          activeReview = undefined;
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
      case "open-file":
        openFile(message.path, message.line, message.preview === true, message.scratch === true);
        return true;
      case "scratch-saved": {
        // The host saved a scratch buffer under a real name (and deleted its temp file). Either convert the
        // tab to the saved file or — when it was saved outside the workspace (the host warned via a toast) —
        // drop the scratch tab; THEN release the scratch model (without flushing — its temp is gone). Switching
        // the editor to the new model before disposing the old mirrors the close path's ordering.
        if (message.savedPath === "") {
          return true; // the user cancelled the save dialog; leave the scratch tab as-is
        }
        if (message.reopen) {
          const result = convertScratch(message.scratchPath, message.savedPath);
          if (result !== null) {
            applyActive(result);
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
        // Claude's close_tab MCP tool: the host resolved the tab name to our path key; close that tab.
        tabs.close(message.path);
        return true;
      case "turn-diff":
        // Inline diff of this turn's changes, shown in the live editor. Equal baseline/current = no markers.
        if (message.baseline === message.current) {
          inlineDiff?.clear(message.path);
        } else {
          inlineDiff?.set(message.path, {
            original: message.baseline,
            claudeVersion: message.current,
            mode: "applied",
            onAccept: () => postToHost({ type: "accept-turn" }),
            onUndo: () => postToHost({ type: "undo-turn" }),
          });
        }
        return true;
      case "turn-reset":
        inlineDiff?.clearAll();
        return true;
      case "change-diff":
        // A file picked in the Changes navigator: show its whole-session diff inline (read-only view — the
        // file is opened via reveal-file alongside this request).
        if (message.baseline === message.current) {
          inlineDiff?.clear(message.path);
        } else {
          inlineDiff?.set(message.path, {
            original: message.baseline,
            claudeVersion: message.current,
            mode: "view",
          });
        }
        return true;
      default:
        return false;
    }
  };

  // New File: ask the host to create a scratch buffer; it comes back as an open-file with `scratch: true`.
  const newFile = (): void => {
    postToHost({ type: "new-scratch" });
  };

  // Save the active editor. A scratch buffer is sent to the host to save under a real name (a native dialog;
  // its pending autosave is cancelled first so nothing re-creates the temp while the dialog is open). A real
  // file is already autosaved, so this just consumes the key. Returns true either way (Ctrl+S is handled).
  const save = (): boolean => {
    const path = activePath();
    if (path === null) {
      return true;
    }
    const entry = openTabs().find((tab) => tab.path === path);
    if (entry?.scratch === true) {
      const content = host?.contentOf(path) ?? "";
      host?.cancelSave(path);
      postToHost({
        type: "save-scratch-as",
        path,
        content,
        suggestedName: basename(path),
      });
    }
    return true;
  };

  return {
    start,
    openFile,
    handleMessage,
    focusEditor: () => host?.editor.focus(),
    newFile,
    save,
    inline: {
      nextChange: () => inlineDiff?.nextChange() ?? false,
      prevChange: () => inlineDiff?.prevChange() ?? false,
      accept: () => inlineDiff?.accept() ?? false,
      reject: () => inlineDiff?.reject() ?? false,
      undo: () => inlineDiff?.undo() ?? false,
    },
    tabs,
    dispose: () => {
      window.clearTimeout(initTimer);
      inlineDiff?.dispose();
      host?.dispose();
    },
  };
}
