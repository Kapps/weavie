// Owns the Monaco editor lifecycle and diff/review orchestration on App's behalf. Drives the editor host +
// inline-diff layer (editor-host.ts / inline-diff.ts).

import { createSignal } from "solid-js";
import { type WebBoundMessage, log, postToHost } from "../bridge";
import { dismissSplash } from "../splash";
import { mark } from "../startup-timing";
import { type CommentProse, createCommentProse } from "./comment-prose";
import type { EditorHost } from "./editor-host";
import { samePath } from "./fs-path";
import {
  type HunkRevert,
  type InlineDiff,
  createInlineDiff,
  firstChangedLine,
} from "./inline-diff";
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
  /** Surface a debounced save that failed to reach disk. */
  onSaveError: (message: string) => void;
  /** Surface a file that couldn't be opened (read), so a failed open errors loudly instead of a blank tab. */
  onOpenError: (message: string) => void;
  /** Report the file the editor is showing so the browser / title bar can track it. */
  onCurrentFileChanged: (path: string | null) => void;
  /** Confirm discarding unsaved scratch buffers about to be closed (`names`); the single close-path guard. */
  confirmDiscard: (names: string[]) => Promise<boolean>;
  /** Confirm a destructive review action (Revert file / Revert all). Resolves true to proceed. */
  confirm: (options: { title: string; body: string; confirmLabel: string }) => Promise<boolean>;
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
}

export interface EditorController {
  /** Loads the editor chunk and brings up the editor in `container`; fades the splash when settled. */
  start(container: HTMLElement): void;
  /** Opens a file (preview tab when `preview`), replaying once the editor chunk has loaded (last wins). */
  openFile(path: string, line: number, preview?: boolean): void;
  /** Handles an editor-related host message; returns false for messages this controller doesn't own. */
  handleMessage(message: WebBoundMessage): boolean;
  /** Focuses the editor (for focus-pane). */
  focusEditor(): void;
  /** New File: asks the host to create a scratch buffer, which comes back as an open-file with `scratch`. */
  newFile(): void;
  /** Save the active editor: a scratch buffer prompts for a name; a real file is already autosaved. */
  save(): boolean;
  /** Update the post-turn review set driving the inline toolbar's ← / → file walk; empty when nothing to review. */
  setReviewFiles(files: ReviewFile[]): void;
  /** Open the first file in the review set landed on its first change (the manual "jump into review"). */
  openFirstReviewFile(): boolean;
  /** The active file's current working-copy text (reactive), for the Preview overlay; "" when none. */
  activeContent(): string;
  /** Whether an inline openDiff review is showing (reactive), so Preview suspends rather than hiding it. */
  reviewActive(): boolean;
  readonly inline: InlineDiffActions;
  readonly tabs: TabActions;
  dispose(): void;
}

export function createEditorController(deps: EditorControllerDeps): EditorController {
  // host + inlineDiff are set once the editor chunk loads and the editor is created (see start).
  let host: EditorHost | undefined;
  let inlineDiff: InlineDiff | undefined;
  let commentProse: CommentProse | undefined;
  let initTimer: number | undefined;
  // Disposables for the content/model listeners that feed activeContent (the live Preview text).
  let contentSubs: { dispose(): void }[] = [];
  // The active file's working-copy text, kept live off the editor model so Preview renders edits/reloads.
  const [activeContent, setActiveContent] = createSignal("");
  // Whether an inline openDiff review currently occupies the editor, so the Preview overlay suspends over it.
  const [reviewActive, setReviewActive] = createSignal(false);
  // An open-file request that arrived before the editor was ready; replayed when it is.
  let pendingOpen: { path: string; line: number; preview?: boolean; scratch?: boolean } | undefined;
  // Files Claude changed since the last review, in document order; drives the toolbar's ← / → file walk.
  let reviewFiles: ReviewFile[] = [];
  // Per-hunk Keep marks (path → reviewed signatures). Web-only; survives reopen + revert recompute; cleared on
  // turn-reset.
  const reviewMarks = new Map<string, Set<string>>();
  // Per-file hunk signatures last rendered, so the Keep walk knows which files still have pending hunks.
  const fileHunks = new Map<string, string[]>();

  const isHunkReviewed = (path: string, signature: string): boolean =>
    reviewMarks.get(path)?.has(signature) ?? false;
  const markHunkReviewed = (path: string, signature: string): void => {
    let marks = reviewMarks.get(path);
    if (marks === undefined) {
      marks = new Set<string>();
      reviewMarks.set(path, marks);
    }
    marks.add(signature);
  };
  // Whether a file still has a pending (un-kept) hunk. An unseen file is treated as pending so the walk opens
  // it to find out; once seen, it's pending iff some signature isn't marked.
  const fileHasPending = (path: string): boolean => {
    const signatures = fileHunks.get(path);
    if (signatures === undefined) {
      return true;
    }
    const marks = reviewMarks.get(path);
    return signatures.some((signature) => !(marks?.has(signature) ?? false));
  };
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
  const applyActive = (result: ActivateResult): void => {
    deps.onCurrentFileChanged(result.path);
    // Don't clobber an in-progress review: the reviewed file is active, but the editor shows the transient
    // review model; re-showing the working copy would drop the diff. resolveReview → endReview restores it.
    if (activeReview !== undefined && samePath(activeReview.path, result.path)) {
      return;
    }
    // If the file can't be read, the editor never swaps its model — close this tab rather than leave it active
    // over a stale/blank pane, and fall back to a surviving neighbor (or clear).
    void host?.show(result.path, result.placement).then((ok) => {
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

  const openFile = (path: string, line: number, preview = false, scratch = false): void => {
    if (host === undefined) {
      deps.onCurrentFileChanged(path); // optimistic; the editor chunk isn't up yet
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
    if (entry.path === wasActive) {
      applyOrClear(result.next);
    }
    releaseClosed(result.disposed, scratch);
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
    const editorReady = import("./editor-host").then(({ createEditorHost }) =>
      createEditorHost(container, deps.onSaveError, deps.onOpenError),
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
        // Track the active model's text so the Preview overlay renders live (edits, Claude writes, reloads).
        const syncContent = (): void => {
          setActiveContent(created.editor.getModel()?.getValue() ?? "");
        };
        contentSubs = [
          created.editor.onDidChangeModelContent(() => syncContent()),
          created.editor.onDidChangeModel(() => syncContent()),
        ];
        syncContent();
        // Suspended over a model with a live inline diff so a collapsed comment never hides a changed line.
        commentProse = createCommentProse(created.editor, {
          isBlocked: (uri) => inlineDiff?.hasDiffForUri(uri) ?? false,
        });
        if (pendingOpen !== undefined) {
          const { path, line, preview, scratch } = pendingOpen;
          pendingOpen = undefined;
          openFile(path, line, preview, scratch);
        }
        // Reflect whatever file the editor ended up showing (replayed pending-open or hot-reload restore).
        const model = created.editor.getModel();
        if (model !== null && model.uri.scheme === "file") {
          deps.onCurrentFileChanged(model.uri.fsPath);
        }
        postToHost({ type: "monaco-ready" });
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
  const openReviewFile = (file: ReviewFile): void => {
    openFile(file.path, file.line, true);
    postToHost({ type: "get-turn-diff", path: file.path });
  };

  // Step the file axis of the review walk: open the neighbour (wrapping) at its first change. Returns false (so
  // $mod+Left/Right keep word-nav) when there's no multi-file review or the active file isn't in it.
  const stepReviewFile = (delta: number): boolean => {
    if (reviewFiles.length < 2) {
      return false;
    }
    const current = activePath();
    const idx = current === null ? -1 : reviewFiles.findIndex((f) => samePath(f.path, current));
    if (idx === -1) {
      return false;
    }
    const next = reviewFiles[(idx + delta + reviewFiles.length) % reviewFiles.length];
    if (next === undefined) {
      return false;
    }
    openReviewFile(next);
    return true;
  };

  // The Keep walk reached the end of a file's hunks: open the next file (wrapping) that still has a pending
  // hunk, on its first change. Skips fully-kept files; when nothing remains pending, finalize the review.
  const advanceToNextPendingFile = (fromPath: string): void => {
    const idx = reviewFiles.findIndex((f) => samePath(f.path, fromPath));
    const start = idx === -1 ? 0 : idx;
    for (let step = 1; step <= reviewFiles.length; step++) {
      const candidate = reviewFiles[(start + step) % reviewFiles.length];
      if (candidate === undefined || samePath(candidate.path, fromPath)) {
        continue;
      }
      if (fileHasPending(candidate.path)) {
        openReviewFile(candidate);
        return;
      }
    }
    // Nothing pending: keeping the last change finalizes the review like Keep-all; the host emits turn-reset
    // to tear the viewer down. Without this the diff + toolbar would linger on screen.
    postToHost({ type: "accept-turn" });
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

  // Revert every change in one file to its turn baseline on disk, after a confirm (the host restores the file
  // wholesale and re-emits its now-empty diff + the trimmed review set).
  const revertFile = (path: string): void => {
    void deps
      .confirm({
        title: "Revert file?",
        body: `Discard all changes to "${basename(path)}" and restore it to before this turn? This can't be undone.`,
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
        body: `Discard every change from this turn${count > 1 ? ` across ${count} files` : ""}? This can't be undone.`,
        confirmLabel: "Revert all",
      })
      .then((ok) => {
        if (ok) {
          postToHost({ type: "undo-turn" });
        }
      });
  };

  const handleMessage = (message: WebBoundMessage): boolean => {
    switch (message.type) {
      case "show-diff": {
        // Render Claude's openDiff proposal inline over a transient review model (the real working copy is
        // never touched): the editor shows `proposed`, diffed vs `original`, with a Keep/Reject toolbar.
        const priorActive = activePath();
        const wasOpen = openTabs().some((tab) => samePath(tab.path, message.path));
        // Reveal the proposal at its first changed hunk, not the top of the file.
        const reviewUri = host?.beginReview(
          message.path,
          message.proposed,
          firstChangedLine(message.original, message.proposed),
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
        // A session switch flipped the store to the incoming session's tab set, so rebind the editor. On launch
        // this arrives before the editor chunk is up; restoreSession in createEditorHost covers that.
        if (host !== undefined) {
          void host.rebindSession().then(() => deps.onCurrentFileChanged(activePath()));
        }
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
        // Claude's close_tab MCP tool: the host resolved the tab name to our path key.
        tabs.close(message.path);
        return true;
      case "turn-diff": {
        // Inline diff of this turn's changes. Equal baseline/current = no markers.
        if (message.baseline === message.current) {
          inlineDiff?.clear(message.path);
          commentProse?.refresh();
          // A revert emptied this file's diff. If it's under review and other changed files remain, walk on to
          // the next so the toolbar follows the review instead of vanishing (same advance Keep does).
          const active = activePath();
          if (active !== null && samePath(active, message.path) && reviewFiles.length > 1) {
            advanceToNextPendingFile(message.path);
          }
          return true;
        }
        // The toolbar's ← / → file axis: only for a multi-file review containing this file, so a single-file
        // review leaves $mod+Left/Right as editor word-nav.
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
          claudeVersion: message.current,
          mode: "applied",
          onKeepHunk: (signature) => markHunkReviewed(message.path, signature),
          isReviewed: (signature) => isHunkReviewed(message.path, signature),
          onRevertHunk: (hunk) => revertHunk(message.path, hunk),
          onRevertFile: () => revertFile(message.path),
          onKeepAll: () => postToHost({ type: "accept-turn" }),
          onUndo: revertAll,
          onHunks: (signatures) => {
            fileHunks.set(message.path, signatures);
          },
          onAdvanceFile: () => advanceToNextPendingFile(message.path),
          // Always present so the stacked toolbar label shows the filename even for a single-file review.
          fileLabel: message.name,
          ...fileNav,
        });
        // The diff suspends comment-prose over this model; refresh so a collapsed comment re-expands rather
        // than hiding a changed line.
        commentProse?.refresh();
        return true;
      }
      case "turn-reset":
        // A turn boundary that clears the set (Keep-all) or a session switch: drop all inline markers and the
        // web-side review state so a fresh set starts clean. reviewFiles too — else a switch with no following
        // turn-changes leaves the ← / → walk pointed at the previous session's (possibly non-existent) paths.
        inlineDiff?.clearAll();
        reviewMarks.clear();
        fileHunks.clear();
        reviewFiles = [];
        commentProse?.refresh();
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
    setReviewFiles: (files) => {
      reviewFiles = files;
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
    },
    tabs,
    dispose: () => {
      window.clearTimeout(initTimer);
      for (const sub of contentSubs) {
        sub.dispose();
      }
      commentProse?.dispose();
      inlineDiff?.dispose();
      host?.dispose();
    },
  };
}
