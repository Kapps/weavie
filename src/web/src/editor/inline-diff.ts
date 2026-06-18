// Renders a diff INSIDE the live code editor (Cursor-style), never a standalone diff viewer: added lines as
// green whole-line decorations, removed lines as red "ghost" view-zones in place, char-level highlights for
// replacements, and a small Accept/Reject(/Undo) toolbar. The modified side is always the editor's LIVE model
// content, so the diff tracks edits live (the user tweaking a proposal, or the working copy reloading after a
// Claude edit). The layer owns only its decorations/zones/widget — it NEVER disposes the host-owned live model.

import { linesDiffComputers } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/diff/linesDiffComputers";
import { currentFonts, onFontsChanged } from "../fonts";
import { monaco } from "./monaco-setup";

const DIFF_OPTIONS = {
  ignoreTrimWhitespace: false,
  maxComputationTimeMs: 1000,
  computeMoves: false,
} as const;

// Debounce diff recompute so typing into a model under review (or a burst of working-copy reloads) doesn't
// recompute + re-lay-out view zones on every keystroke.
const RECOMPUTE_DEBOUNCE_MS = 120;

export type InlineDiffMode = "review" | "applied" | "view";

export interface InlineDiffOptions {
  /** The baseline/original text the live model is diffed against. */
  original: string;
  /**
   * review = a pending openDiff proposal (Keep/Reject gate); applied = a turn's already-applied changes
   * (Accept clears the markers, Undo reverts the set); view = a read-only diff (e.g. browsing a session
   * change), no toolbar.
   */
  mode: InlineDiffMode;
  /** Resolve a review (Keep) or dismiss applied markers (Accept). */
  onAccept?: () => void;
  /** Review only: reject the proposal. */
  onReject?: () => void;
  /** Applied only: revert the change set. */
  onUndo?: () => void;
}

/** Per-editor inline-diff controller. Diffs are keyed by file path; only the editor's current model renders. */
export interface InlineDiff {
  /** Register (or replace) the diff for a file path; renders immediately if that file is the active model. */
  set(path: string, options: InlineDiffOptions): void;
  /** Remove the diff for a file path. */
  clear(path: string): void;
  /**
   * Register the diff keyed by an exact model URI string (not a file path) — used for the transient
   * `weavie-review:` model an openDiff review renders over. Renders immediately if it's the active model.
   */
  setByUri(uri: string, options: InlineDiffOptions): void;
  /** Remove the diff registered by an exact model URI string (the review-model counterpart of clear). */
  clearByUri(uri: string): void;
  /** Remove every registered diff. */
  clearAll(): void;
  // The nav/action methods return whether they acted, so an unmatched keybinding (no active diff, or the
  // action unavailable in this mode) falls through to the editor.
  /** Jump to the next change hunk in the active diff. */
  nextChange(): boolean;
  /** Jump to the previous change hunk in the active diff. */
  prevChange(): boolean;
  /** Accept the active diff (review Keep / applied Accept). */
  accept(): boolean;
  /** Reject the active review proposal. */
  reject(): boolean;
  /** Undo the active applied turn. */
  undo(): boolean;
  /** Tear down listeners + any rendered markers (never disposes a model). */
  dispose(): void;
}

// Split a string into lines the way a Monaco model does (so `original` lines line up with getLinesContent()).
function splitLines(text: string): string[] {
  return text.replace(/\r\n?/g, "\n").split("\n");
}

/** Creates an inline-diff controller bound to `editor`. */
export function createInlineDiff(editor: monaco.editor.IStandaloneCodeEditor): InlineDiff {
  const diffs = new Map<string, InlineDiffOptions>();
  let decorations: monaco.editor.IEditorDecorationsCollection | undefined;
  let zoneIds: string[] = [];
  // The floating action bar is a plain DOM child of the editor (not a Monaco overlay widget) so it sits
  // above sticky-scroll and clear of the minimap, positioned bottom-center via CSS.
  let toolbarNode: HTMLElement | undefined;
  let renderedUri: string | undefined;
  let recomputeTimer: ReturnType<typeof setTimeout> | undefined;
  // The currently-rendered diff's options + change anchor lines, so the nav/action methods (driven by the
  // toolbar, keybindings, the palette, or Claude's runCommand) all operate on the active diff.
  let currentOptions: InlineDiffOptions | undefined;
  let currentChangeLines: number[] = [];

  const clearRender = (): void => {
    decorations?.clear();
    decorations = undefined;
    if (zoneIds.length > 0) {
      editor.changeViewZones((accessor) => {
        for (const id of zoneIds) {
          accessor.removeZone(id);
        }
      });
      zoneIds = [];
    }
    toolbarNode?.remove();
    toolbarNode = undefined;
    currentOptions = undefined;
    currentChangeLines = [];
    renderedUri = undefined;
  };

  const buildGhost = (lines: string[]): HTMLElement => {
    const node = document.createElement("div");
    node.className = "weavie-inline-removed";
    const font = currentFonts().editor;
    node.style.fontFamily = font.family;
    node.style.fontSize = `${font.size}px`;
    for (const line of lines) {
      const row = document.createElement("div");
      row.className = "weavie-inline-removed-line";
      row.textContent = line.length === 0 ? " " : line;
      node.appendChild(row);
    }
    return node;
  };

  const makeButton = (className: string, label: string, onClick: () => void): HTMLButtonElement => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = className;
    button.textContent = label;
    button.addEventListener("click", () => onClick());
    return button;
  };

  // Jump the cursor/viewport to the previous/next change hunk (by modified-side anchor line), wrapping.
  // Returns false when there's no diff to navigate, so a keybinding falls through to normal editing.
  const goToChange = (direction: 1 | -1): boolean => {
    if (currentChangeLines.length === 0) {
      return false;
    }
    const current = editor.getPosition()?.lineNumber ?? 1;
    let target: number;
    if (direction === 1) {
      target = currentChangeLines.find((line) => line > current) ?? currentChangeLines[0]!;
    } else {
      const before = currentChangeLines.filter((line) => line < current);
      target =
        before.length > 0
          ? before[before.length - 1]!
          : currentChangeLines[currentChangeLines.length - 1]!;
    }
    editor.revealLineInCenter(target);
    editor.setPosition({ lineNumber: target, column: 1 });
    editor.focus();
    return true;
  };

  // The diff actions, operating on the active diff — shared by the toolbar buttons and the registered
  // commands (keybindings / palette / Claude's runCommand). Each returns whether it acted, so an unmatched
  // keybinding (no diff, or the action not available in this mode) falls through to the editor.
  const nextChange = (): boolean => goToChange(1);
  const prevChange = (): boolean => goToChange(-1);
  const runAction = (action: (() => void) | undefined): boolean => {
    if (action === undefined) {
      return false;
    }
    action();
    return true;
  };
  const accept = (): boolean => runAction(currentOptions?.onAccept);
  const reject = (): boolean => runAction(currentOptions?.onReject);
  const undo = (): boolean => runAction(currentOptions?.onUndo);

  // The floating action bar: prev/next-change arrows (always, when there are hunks) + the mode's actions
  // (Keep/Reject for a review, Accept/Undo for an applied turn, none for a read-only view).
  const buildToolbar = (options: InlineDiffOptions): HTMLElement => {
    const bar = document.createElement("div");
    bar.className = "weavie-inline-toolbar";

    const prev = makeButton("weavie-inline-nav", "↑", prevChange);
    prev.title = "Previous change";
    const next = makeButton("weavie-inline-nav", "↓", nextChange);
    next.title = "Next change";
    bar.append(prev, next);

    if (options.mode === "review") {
      if (options.onAccept !== undefined) {
        bar.appendChild(makeButton("weavie-inline-accept", "Keep", accept));
      }
      if (options.onReject !== undefined) {
        bar.appendChild(makeButton("weavie-inline-reject", "Reject", reject));
      }
    } else if (options.mode === "applied") {
      if (options.onAccept !== undefined) {
        bar.appendChild(makeButton("weavie-inline-accept", "Accept", accept));
      }
      if (options.onUndo !== undefined) {
        bar.appendChild(makeButton("weavie-inline-undo", "Undo", undo));
      }
    }
    return bar;
  };

  const render = (uriString: string): void => {
    clearRender();
    const model = editor.getModel();
    if (model === null || model.uri.toString() !== uriString) {
      return;
    }
    const options = diffs.get(uriString);
    if (options === undefined) {
      return;
    }

    const original = splitLines(options.original);
    const modified = model.getLinesContent();
    const { changes } = linesDiffComputers
      .getDefault()
      .computeDiff(original, modified, DIFF_OPTIONS);
    if (changes.length === 0) {
      return; // no net change (e.g. a turn that reverted itself) — nothing to render
    }

    const deltas: monaco.editor.IModelDeltaDecoration[] = [];
    const ghosts: { afterLineNumber: number; lines: string[] }[] = [];
    const changeLines: number[] = [];

    for (const change of changes) {
      changeLines.push(Math.max(1, change.modified.startLineNumber));
      if (!change.modified.isEmpty) {
        deltas.push({
          range: new monaco.Range(
            change.modified.startLineNumber,
            1,
            change.modified.endLineNumberExclusive - 1,
            1,
          ),
          options: {
            isWholeLine: true,
            className: "weavie-inline-added",
            linesDecorationsClassName: "weavie-inline-added-gutter",
            overviewRuler: {
              color: "rgba(78, 201, 120, 0.7)",
              position: monaco.editor.OverviewRulerLane.Left,
            },
          },
        });
        for (const inner of change.innerChanges ?? []) {
          const r = inner.modifiedRange;
          const empty = r.startLineNumber === r.endLineNumber && r.startColumn === r.endColumn;
          if (!empty) {
            deltas.push({ range: r, options: { inlineClassName: "weavie-inline-added-text" } });
          }
        }
      }
      if (!change.original.isEmpty) {
        ghosts.push({
          afterLineNumber: Math.max(0, change.modified.startLineNumber - 1),
          lines: original.slice(
            change.original.startLineNumber - 1,
            change.original.endLineNumberExclusive - 1,
          ),
        });
      }
    }

    decorations = editor.createDecorationsCollection(deltas);
    editor.changeViewZones((accessor) => {
      for (const ghost of ghosts) {
        zoneIds.push(
          accessor.addZone({
            afterLineNumber: ghost.afterLineNumber,
            heightInLines: ghost.lines.length,
            domNode: buildGhost(ghost.lines),
          }),
        );
      }
    });

    currentOptions = options;
    currentChangeLines = changeLines;
    const editorDom = editor.getDomNode();
    if (editorDom !== null) {
      toolbarNode = buildToolbar(options);
      editorDom.appendChild(toolbarNode);
    }
    renderedUri = uriString;
  };

  // Re-render the active model's diff if it has one, else clear.
  const renderActive = (): void => {
    const model = editor.getModel();
    if (model !== null && diffs.has(model.uri.toString())) {
      render(model.uri.toString());
    } else {
      clearRender();
    }
  };

  const scheduleRender = (): void => {
    if (recomputeTimer !== undefined) {
      clearTimeout(recomputeTimer);
    }
    recomputeTimer = setTimeout(() => {
      recomputeTimer = undefined;
      renderActive();
    }, RECOMPUTE_DEBOUNCE_MS);
  };

  // View zones are lost on model swap — re-render for the newly active model.
  const onModel = editor.onDidChangeModel(renderActive);
  const onContent = editor.onDidChangeModelContent(scheduleRender);
  const offFonts = onFontsChanged(renderActive);

  // Register/remove a diff keyed by an exact model URI string (the path-based set/clear convert a file path
  // to its file:// URI; the review path passes the transient model's URI directly).
  const setByUri = (key: string, options: InlineDiffOptions): void => {
    diffs.set(key, options);
    const model = editor.getModel();
    if (model !== null && model.uri.toString() === key) {
      render(key);
    }
  };
  const clearByUri = (key: string): void => {
    diffs.delete(key);
    if (renderedUri === key) {
      clearRender();
    }
  };

  return {
    set(path, options) {
      setByUri(monaco.Uri.file(path).toString(), options);
    },
    clear(path) {
      clearByUri(monaco.Uri.file(path).toString());
    },
    setByUri,
    clearByUri,
    clearAll() {
      diffs.clear();
      clearRender();
    },
    nextChange,
    prevChange,
    accept,
    reject,
    undo,
    dispose() {
      if (recomputeTimer !== undefined) {
        clearTimeout(recomputeTimer);
      }
      onModel.dispose();
      onContent.dispose();
      offFonts();
      clearRender();
    },
  };
}
