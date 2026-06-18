// Owns the Monaco editor lifecycle and all diff/review orchestration on App's behalf: the deferred
// editor-chunk load (kept off the first-paint path), the openDiff inline-review handshake, and the inline
// diffs for applied turns and session-change browsing. App wires this to host messages and commands; the
// editor host + inline-diff layer it drives live in editor-host.ts / inline-diff.ts.

import { type WebBoundMessage, log, postToHost } from "../bridge";
import { dismissSplash } from "../splash";
import { mark } from "../startup-timing";
import type { EditorHost } from "./editor-host";
import { type InlineDiff, createInlineDiff } from "./inline-diff";

// Generous: only a genuine hang trips it, not a slow cold start.
const EDITOR_INIT_MS = 15_000;

export interface EditorControllerDeps {
  /** Surface a debounced save that failed to reach disk (never a silent drop). */
  onSaveError: (message: string) => void;
  /** Report the file the editor is showing so the browser / title bar can track it. */
  onCurrentFileChanged: (path: string | null) => void;
}

/** Diff nav + actions, exposed so commands (keybindings / palette / Claude) drive the active diff. */
export interface InlineDiffActions {
  nextChange(): boolean;
  prevChange(): boolean;
  accept(): boolean;
  reject(): boolean;
  undo(): boolean;
}

export interface EditorController {
  /** Loads the editor chunk and brings up the editor in `container`; fades the splash when settled. */
  start(container: HTMLElement): void;
  /** Opens a file, replaying once the editor chunk has loaded if it isn't ready yet (last wins). */
  openFile(path: string, line: number): void;
  /** Handles an editor-related host message; returns false for messages this controller doesn't own. */
  handleMessage(message: WebBoundMessage): boolean;
  /** Focuses the editor (for focus-pane). */
  focusEditor(): void;
  readonly inline: InlineDiffActions;
  dispose(): void;
}

export function createEditorController(deps: EditorControllerDeps): EditorController {
  // host + inlineDiff are set once the editor chunk loads and the editor is created (see start).
  let host: EditorHost | undefined;
  let inlineDiff: InlineDiff | undefined;
  let initTimer: number | undefined;
  // An open-file request that arrived before the editor was ready; replayed when it is.
  let pendingOpen: { path: string; line: number } | undefined;
  // The openDiff under inline review. openDiff blocks per-edit, so at most one is live at a time. `reviewUri`
  // is the transient review model's URI the inline diff is keyed by (review never touches the real file).
  let activeReview:
    | { id: string; path: string; original: string; reviewUri: string | undefined }
    | undefined;

  const openFile = (path: string, line: number): void => {
    deps.onCurrentFileChanged(path);
    if (host !== undefined) {
      // The working copy resolves its content from disk through the file provider; no need to pass content.
      host.openFile(path, line);
    } else {
      pendingOpen = { path, line };
    }
  };

  const resolveReview = (keep: boolean): void => {
    const review = activeReview;
    if (review === undefined) {
      return;
    }
    activeReview = undefined;
    // endReview returns the proposal's final (possibly tweaked) content, which Claude writes to disk on keep,
    // and swaps the editor back to the real file. The review never dirtied the file working copy.
    const finalContents = host?.endReview(review.path, keep, review.original) ?? "";
    if (review.reviewUri !== undefined) {
      inlineDiff?.clearByUri(review.reviewUri);
    }
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
          created.openFile(pendingOpen.path, pendingOpen.line);
          pendingOpen = undefined;
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
        const reviewUri = host?.beginReview(message.path, message.proposed, 1);
        activeReview = {
          id: message.id,
          path: message.path,
          original: message.original,
          reviewUri,
        };
        if (reviewUri !== undefined) {
          inlineDiff?.setByUri(reviewUri, {
            original: message.original,
            mode: "review",
            onAccept: () => resolveReview(true),
            onReject: () => resolveReview(false),
          });
        }
        return true;
      }
      case "close-diff":
        // Host cancelled the openDiff: tear the review down without replying — the host's awaiting task is
        // already cancelled.
        if (activeReview?.id === message.id) {
          host?.endReview(activeReview.path, false, activeReview.original);
          if (activeReview.reviewUri !== undefined) {
            inlineDiff?.clearByUri(activeReview.reviewUri);
          }
          activeReview = undefined;
        }
        return true;
      case "open-file":
        openFile(message.path, message.line);
        return true;
      case "turn-diff":
        // Inline diff of this turn's changes, shown in the live editor. Equal baseline/current = no markers.
        if (message.baseline === message.current) {
          inlineDiff?.clear(message.path);
        } else {
          inlineDiff?.set(message.path, {
            original: message.baseline,
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
          inlineDiff?.set(message.path, { original: message.baseline, mode: "view" });
        }
        return true;
      default:
        return false;
    }
  };

  return {
    start,
    openFile,
    handleMessage,
    focusEditor: () => host?.editor.focus(),
    inline: {
      nextChange: () => inlineDiff?.nextChange() ?? false,
      prevChange: () => inlineDiff?.prevChange() ?? false,
      accept: () => inlineDiff?.accept() ?? false,
      reject: () => inlineDiff?.reject() ?? false,
      undo: () => inlineDiff?.undo() ?? false,
    },
    dispose: () => {
      window.clearTimeout(initTimer);
      inlineDiff?.dispose();
      host?.dispose();
    },
  };
}
