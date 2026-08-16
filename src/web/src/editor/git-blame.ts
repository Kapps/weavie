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
import { applyEdit, type BlameSnapshot, blameAt, blameLabel, EMPTY_BLAME } from "./blame-model";
import { openBlame } from "./blame-store";
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
    decorations.clear();
    // Labels are per-file (and go stale as "3 days ago" becomes "4 days ago"), so the cache lives no longer
    // than the model it was built for.
    optionsByLabel.clear();
  };

  const load = (): void => {
    const model = fileModel();
    if (mode === "off" || model === null) {
      clear();
      return;
    }
    const session = sessionForUri(model.uri);
    if (session === undefined) {
      clear();
      return;
    }
    const uri = model.uri.toString();
    const token = ++loadToken;
    void session
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
    const lines: number[] = [];
    for (const range of editor.getVisibleRanges()) {
      const from = Math.max(range.startLineNumber - OVERSCAN, 1);
      const to = Math.min(range.endLineNumber + OVERSCAN, model.getLineCount());
      for (let line = from; line <= to; line++) {
        lines.push(line);
      }
    }
    return lines;
  };

  const render = (): void => {
    const model = fileModel();
    if (mode === "off" || model === null || model.uri.toString() !== loadedUri) {
      decorations.clear();
      return;
    }
    const now = Date.now() / 1000;
    const deltas: monaco.editor.IModelDeltaDecoration[] = [];
    for (const line of annotatedLines(model)) {
      const blamed = blameAt(snapshot, line);
      if (blamed === null) {
        continue;
      }
      const column = model.getLineMaxColumn(line);
      deltas.push({
        range: new monaco.Range(line, column, line, column),
        options: optionsFor(blameLabel(blamed.commit, now)),
      });
    }
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
    const wasOff = mode === "off";
    mode = options.gitBlame;
    if (wasOff) {
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
        load();
      }
    }),
  );

  load();

  return {
    showAtCursor: () => {
      const position = editor.getPosition();
      const visible = position === null ? null : editor.getScrolledVisiblePosition(position);
      const container = editor.getDomNode();
      if (position === null || visible === null || container === null) {
        return false;
      }
      const bounds = container.getBoundingClientRect();
      const top = bounds.top + visible.top;
      if (
        open(position.lineNumber, new DOMRect(bounds.left + visible.left, top, 0, visible.height))
      ) {
        return true;
      }
      // Asked directly and there's no answer: say why. Git's own reason when it gave one (untracked file, no
      // repository), otherwise the line itself is simply newer than the last save.
      notify(
        "info",
        blameError ?? "That line isn't in a commit yet — save the file to blame it.",
        "weavie-blame",
      );
      return true;
    },
    dispose: () => {
      loadToken++;
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
