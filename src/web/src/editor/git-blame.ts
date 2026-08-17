// Renders who last changed each line as faded text at the end of it, and opens the blame popover when one is
// clicked. Blame is a property of the file on disk, so it is fetched per model and re-fetched when the file
// changes there; in between, buffer edits re-align it line-by-line (blame-model.ts) rather than leaving stale
// annotations sitting beside lines they no longer describe.
//
// Only the visible lines are decorated, re-run on scroll: a long file's blame is thousands of lines, and
// injected text costs per rendered line.

import { type GitBlameMode, registerSessionFeature } from "../bridge";
import { currentEditorOptions, onEditorOptionsChanged } from "../editor-options";
import { notify } from "../notify/notify";
import {
  applyEdit,
  type BlameSnapshot,
  blameAt,
  blameLabel,
  EMPTY_BLAME,
  startsRun,
} from "./blame-model";
import { closeBlame, openBlame } from "./blame-store";
import { normalizePath } from "./fs-path";
import { monaco } from "./monaco-setup";
import { SESSION_FILE_SCHEME, sessionForUri, sessionUriHostPath } from "./session-uri";

/** The CSS class on the injected annotation — also the click target's marker. */
const BLAME_CLASS = "weavie-blame";

// Keeps the annotation clear of the code it follows, in the editor's own monospace advance.
const GAP = "    ";

// Lines above and below the viewport, so an ordinary scroll reveals annotated lines rather than blank ones that
// fill in a frame later.
const OVERSCAN = 20;

// A file can be rewritten many times in one agent turn, and each write reports separately. Blame only has to
// describe where the writes settled, so a burst collapses into one probe instead of one per write.
const RELOAD_DEBOUNCE_MS = 150;

interface BlameResponse {
  commits: BlameSnapshot["commits"];
  lineCommits: number[];
  lineOriginals: number[];
  error: string | null;
}

interface FileChange {
  path: string;
  kind: string;
}

export interface GitBlameController {
  /**
   * Opens the popover for the cursor's line, or reports why that line has no commit behind it. Always handles
   * the request — the caller declines only when no editor is mounted at all.
   */
  showAtCursor(): boolean;
  /** Drops decorations, listeners, and the pending fetch. */
  dispose(): void;
}

/** Creates the blame controller bound to `editor`. */
export function createGitBlame(editor: monaco.editor.IStandaloneCodeEditor): GitBlameController {
  let mode: GitBlameMode = currentEditorOptions().gitBlame;
  let snapshot: BlameSnapshot = EMPTY_BLAME;
  // The model the loaded snapshot belongs to; a stale response for a since-swapped model is dropped on arrival.
  let loadedUri: string | null = null;
  // Why the loaded file has no blame, when Git gave a reason. Reported only when the user explicitly asks.
  let blameError: string | null = null;
  let loadToken = 0;
  let frame: number | undefined;
  // What the last applied decoration set was, so an identical render is skipped outright.
  let renderedKey = "";
  let reloadTimer: ReturnType<typeof setTimeout> | undefined;
  const decorations = editor.createDecorationsCollection([]);
  // One options object per distinct label, so scrolling back over a line reuses it instead of allocating anew.
  const optionsByLabel = new Map<string, monaco.editor.IModelDecorationOptions>();

  const fileModel = (): monaco.editor.ITextModel | null => {
    const model = editor.getModel();
    return model !== null && model.uri.scheme === SESSION_FILE_SCHEME ? model : null;
  };

  const clear = (): void => {
    snapshot = EMPTY_BLAME;
    blameError = null;
    loadedUri = null;
    renderedKey = "";
    decorations.clear();
    // Labels are per-file (and go stale as "3 days ago" becomes "4 days ago"), so the cache lives no longer
    // than the model it was built for.
    optionsByLabel.clear();
  };

  // The automatic path: annotations off means no probe at all, so turning blame off costs nothing per file.
  const load = (): void => {
    if (mode === "off") {
      clear();
      return;
    }
    void fetchBlame();
  };

  // Loads the active model's blame regardless of mode, so an explicit Show Blame answers even with
  // annotations off. Resolves once the snapshot is in place (or known unavailable).
  const fetchBlame = (): Promise<void> => {
    const model = fileModel();
    if (model === null) {
      clear();
      return Promise.resolve();
    }
    const session = sessionForUri(model.uri);
    if (session === undefined) {
      clear();
      return Promise.resolve();
    }
    const uri = model.uri.toString();
    const token = ++loadToken;
    return session
      .feature("git")
      .request<BlameResponse, { path: string }>("blame", { path: sessionUriHostPath(model.uri) })
      .then((response) => {
        if (token !== loadToken) {
          return;
        }
        // A file Git won't blame — untracked, binary, ignored — has no annotations, which is the honest
        // rendering of "Git has nothing to say about these lines". The reason is kept so an explicit Show
        // Blame can answer with it rather than declining a keystroke for no visible reason.
        snapshot =
          response.error === null
            ? {
                commits: response.commits,
                lineCommits: response.lineCommits,
                lineOriginals: response.lineOriginals,
              }
            : EMPTY_BLAME;
        blameError = response.error;
        loadedUri = uri;
        render();
      })
      .catch((error: unknown) => {
        if (token === loadToken) {
          clear();
          blameError = error instanceof Error ? error.message : String(error);
          loadedUri = uri;
        }
      });
  };

  const optionsFor = (label: string): monaco.editor.IModelDecorationOptions => {
    const existing = optionsByLabel.get(label);
    if (existing !== undefined) {
      return existing;
    }
    const created: monaco.editor.IModelDecorationOptions = {
      // NeverGrowsWhenTypingAtEdges: typing at the end of an annotated line extends the line, not the marker.
      stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
      // The anchor is a zero-width range at the end of the line — it marks a position, not a span — and
      // Monaco discards injected text on an empty range unless told otherwise
      // (textModel.ts: getAllInjectedText filters `showIfCollapsed || !range.isEmpty()`). Without this the
      // decorations exist on the model and nothing whatsoever paints.
      showIfCollapsed: true,
      after: {
        content: GAP + label,
        inlineClassName: BLAME_CLASS,
        // The annotation is not text: End / Right-arrow at the end of a line must stop at the code, never
        // walk the caret into the label.
        cursorStops: monaco.editor.InjectedTextCursorStops.None,
        // It renders at a smaller size than the code, so Monaco can't assume the editor's uniform advance
        // when measuring it.
        inlineClassNameAffectsLetterSpacing: true,
      },
    };
    optionsByLabel.set(label, created);
    return created;
  };

  // The lines to annotate: every visible one in `all`, only the cursor's in `currentLine`.
  const annotatedLines = (model: monaco.editor.ITextModel): number[] => {
    if (mode === "currentLine") {
      const line = editor.getPosition()?.lineNumber;
      return line === undefined || line > model.getLineCount() ? [] : [line];
    }
    // A set, not a list: a collapsed region (comment prose, folding) splits the viewport into several ranges,
    // and widening each by the overscan makes neighbouring ones overlap — so a line lands in two windows and
    // would otherwise be decorated, and painted, twice.
    const lines = new Set<number>();
    for (const range of editor.getVisibleRanges()) {
      const from = Math.max(range.startLineNumber - OVERSCAN, 1);
      const to = Math.min(range.endLineNumber + OVERSCAN, model.getLineCount());
      for (let line = from; line <= to; line++) {
        lines.add(line);
      }
    }
    return [...lines];
  };

  const render = (): void => {
    const model = fileModel();
    if (mode === "off" || model === null || model.uri.toString() !== loadedUri) {
      decorations.clear();
      return;
    }
    const now = Date.now() / 1000;
    const deltas: monaco.editor.IModelDeltaDecoration[] = [];
    // What this render would produce, so an unchanged one costs nothing. Scrolling fires continuously but only
    // crosses a line boundary occasionally, and replacing the whole decoration collection makes Monaco redo
    // every annotated line — which is the expensive half, since injected text takes a line off its fast
    // render path.
    let key = "";
    for (const line of annotatedLines(model)) {
      const blamed = blameAt(snapshot, line);
      // In `all`, label only where a commit's run begins: one commit usually owns a stretch of consecutive
      // lines, and repeating it down every one of them is what makes the whole file unreadable. Keyed off the
      // file, not the viewport, so scrolling never moves a label. `currentLine` always labels the cursor's
      // line — the point there is to answer for that exact line.
      if (blamed === null || (mode === "all" && !startsRun(snapshot, line))) {
        continue;
      }
      const column = model.getLineMaxColumn(line);
      const label = blameLabel(blamed.commit, now);
      key += `${line} ${label}`;
      deltas.push({
        range: new monaco.Range(line, column, line, column),
        options: optionsFor(label),
      });
    }
    if (key === renderedKey) {
      return;
    }
    renderedKey = key;
    decorations.set(deltas);
  };

  const scheduleRender = (): void => {
    if (frame !== undefined) {
      return;
    }
    frame = requestAnimationFrame(() => {
      frame = undefined;
      render();
    });
  };

  // Opens the popover for `line`, anchored on `anchor`. False when the line carries no attribution.
  const open = (line: number, anchor: DOMRect): boolean => {
    const model = fileModel();
    const blamed = model === null ? null : blameAt(snapshot, line);
    if (model === null || blamed === null) {
      return false;
    }
    const session = sessionForUri(model.uri);
    if (session === undefined) {
      return false;
    }
    openBlame({
      session,
      path: sessionUriHostPath(model.uri),
      line,
      blamed,
      anchor: { left: anchor.left, right: anchor.right, top: anchor.top, bottom: anchor.bottom },
    });
    return true;
  };

  const subscriptions: monaco.IDisposable[] = [
    editor.onDidChangeModel(() => {
      // The popover describes a line of the model being replaced; a keyboard file switch never produces the
      // outside pointerdown that would otherwise dismiss it.
      closeBlame();
      clear();
      load();
    }),
    editor.onDidChangeModelContent((event) => {
      // A model reset (the host reloaded the file from disk) invalidates the blame outright; ordinary edits only
      // move lines around, so the snapshot is re-aligned instead of re-fetched on every keystroke.
      if (event.isFlush) {
        load();
        return;
      }
      // Monaco orders changes from the end of the document backwards, so applying them in sequence keeps each
      // splice's line numbers valid.
      for (const change of event.changes) {
        snapshot = applyEdit(snapshot, {
          startLine: change.range.startLineNumber,
          removedLines: change.range.endLineNumber - change.range.startLineNumber,
          addedLines: change.text.split("\n").length - 1,
          fromLineStart: change.range.startColumn === 1,
        });
      }
      scheduleRender();
    }),
    editor.onDidScrollChange(scheduleRender),
    editor.onDidLayoutChange(scheduleRender),
    editor.onDidChangeCursorPosition(() => {
      if (mode === "currentLine") {
        scheduleRender();
      }
    }),
    editor.onMouseDown((event) => {
      const annotation = event.target.element?.closest(`.${BLAME_CLASS}`);
      const line = event.target.position?.lineNumber;
      if (
        annotation != null &&
        line !== undefined &&
        open(line, annotation.getBoundingClientRect())
      ) {
        // Swallow the click so it neither moves the caret to the line end nor starts a selection drag.
        event.event.preventDefault();
        event.event.stopPropagation();
      }
    }),
  ];

  const offOptions = onEditorOptionsChanged((options) => {
    if (options.gitBlame === mode) {
      return;
    }
    mode = options.gitBlame;
    // Turning them off drops the snapshot too, so nothing stale survives to answer a later question from.
    // Sticky scroll keeps its own rendered copy of the pinned lines and rebuilds it only on scroll, so a label
    // can outlive the decorations up there until the next scroll. `editor.render(true)` does not reach that
    // copy — driving the real app proved it — so there is nothing here that would honestly fix it. See
    // docs/specs/git-blame.md.
    if (mode === "off") {
      clear();
    } else if (loadedUri === null) {
      load();
    } else {
      render();
    }
  });

  // The file changing on disk — a save landing, an agent write, a checkout — is what actually invalidates blame.
  // Every live session is watched, not just the selected one: the editor shows whichever session owns its model.
  const offFiles = registerSessionFeature((session) =>
    session.feature("files").on<{ changes: FileChange[] }>("changed", (message) => {
      const model = fileModel();
      if (model === null) {
        return;
      }
      const active = normalizePath(sessionUriHostPath(model.uri));
      if (message.changes.some((change) => normalizePath(change.path) === active)) {
        clearTimeout(reloadTimer);
        reloadTimer = setTimeout(load, RELOAD_DEBOUNCE_MS);
      }
    }),
  );

  load();

  // Opens the cursor's line, or explains why it has no commit. Git's own reason when it gave one (untracked
  // file, no repository); otherwise the line is simply newer than the last save.
  const openCursorOrExplain = (line: number): void => {
    const visible = editor.getScrolledVisiblePosition({ lineNumber: line, column: 1 });
    const container = editor.getDomNode();
    if (visible === null || container === null) {
      return;
    }
    const bounds = container.getBoundingClientRect();
    const anchor = new DOMRect(
      bounds.left + visible.left,
      bounds.top + visible.top,
      0,
      visible.height,
    );
    if (!open(line, anchor)) {
      notify(
        "info",
        blameError ?? "That line isn't in a commit yet — save the file to blame it.",
        "weavie-blame",
      );
    }
  };

  return {
    showAtCursor: () => {
      const line = editor.getPosition()?.lineNumber;
      const model = fileModel();
      if (line === undefined || model === null) {
        return false;
      }
      // Asking for a line's blame is a question about the file, not about whether annotations are painted —
      // so with them off (nothing loaded) fetch on demand rather than answering from an empty snapshot.
      if (loadedUri === model.uri.toString()) {
        openCursorOrExplain(line);
      } else {
        void fetchBlame().then(() => openCursorOrExplain(line));
      }
      return true;
    },
    dispose: () => {
      loadToken++;
      // Leaving it open would strand the panel on a torn-down session, whose bus rejects every request.
      closeBlame();
      clearTimeout(reloadTimer);
      if (frame !== undefined) {
        cancelAnimationFrame(frame);
      }
      for (const subscription of subscriptions) {
        subscription.dispose();
      }
      offOptions();
      offFiles();
      decorations.clear();
    },
  };
}
