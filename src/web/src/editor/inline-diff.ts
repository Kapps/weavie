// Renders a diff INSIDE the live code editor (Cursor-style), never a standalone diff viewer: added lines as
// green whole-line decorations, removed lines as red "ghost" view-zones in place, char-level highlights for
// replacements, and a small Accept/Reject(/Undo) toolbar. The modified side is always the editor's LIVE model
// content, so the diff tracks edits live (the user tweaking a proposal, or a host refresh-file landing). The
// layer owns only its decorations/zones/widget — it NEVER disposes the host-owned live model.

import { linesDiffComputers } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/diff/linesDiffComputers";
import { currentFonts, onFontsChanged } from "../fonts";
import { monaco } from "./monaco-setup";

const DIFF_OPTIONS = {
  ignoreTrimWhitespace: false,
  maxComputationTimeMs: 1000,
  computeMoves: false,
} as const;

// Debounce diff recompute so typing into a model under review (or a burst of refresh-file pushes) doesn't
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
  /** Remove every registered diff. */
  clearAll(): void;
  /** Tear down listeners + any rendered markers (never disposes a model). */
  dispose(): void;
}

// Split a string into lines the way a Monaco model does (so `original` lines line up with getLinesContent()).
function splitLines(text: string): string[] {
  return text.replace(/\r\n?/g, "\n").split("\n");
}

/** Creates an inline-diff controller bound to <paramref>editor</paramref>. */
export function createInlineDiff(editor: monaco.editor.IStandaloneCodeEditor): InlineDiff {
  const diffs = new Map<string, InlineDiffOptions>();
  let decorations: monaco.editor.IEditorDecorationsCollection | undefined;
  let zoneIds: string[] = [];
  let widget: monaco.editor.IOverlayWidget | undefined;
  let renderedUri: string | undefined;
  let recomputeTimer: ReturnType<typeof setTimeout> | undefined;

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
    if (widget !== undefined) {
      editor.removeOverlayWidget(widget);
      widget = undefined;
    }
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

  // The Accept/Reject(/Undo) toolbar for the active diff, or null for a read-only `view` diff (no actions).
  const buildToolbar = (options: InlineDiffOptions): HTMLElement | null => {
    const buttons: HTMLButtonElement[] = [];
    if (options.mode === "review") {
      if (options.onAccept !== undefined) {
        buttons.push(makeButton("weavie-inline-accept", "Keep", options.onAccept));
      }
      if (options.onReject !== undefined) {
        buttons.push(makeButton("weavie-inline-reject", "Reject", options.onReject));
      }
    } else if (options.mode === "applied") {
      if (options.onAccept !== undefined) {
        buttons.push(makeButton("weavie-inline-accept", "Accept", options.onAccept));
      }
      if (options.onUndo !== undefined) {
        buttons.push(makeButton("weavie-inline-undo", "Undo", options.onUndo));
      }
    }
    if (buttons.length === 0) {
      return null;
    }
    const bar = document.createElement("div");
    bar.className = "weavie-inline-toolbar";
    for (const button of buttons) {
      bar.appendChild(button);
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

    for (const change of changes) {
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

    const toolbar = buildToolbar(options);
    if (toolbar !== null) {
      widget = {
        getId: () => "weavie.inline-diff.toolbar",
        getDomNode: () => toolbar,
        getPosition: () => ({
          preference: monaco.editor.OverlayWidgetPositionPreference.TOP_RIGHT_CORNER,
        }),
      };
      editor.addOverlayWidget(widget);
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

  return {
    set(path, options) {
      const key = monaco.Uri.file(path).toString();
      diffs.set(key, options);
      const model = editor.getModel();
      if (model !== null && model.uri.toString() === key) {
        render(key);
      }
    },
    clear(path) {
      const key = monaco.Uri.file(path).toString();
      diffs.delete(key);
      if (renderedUri === key) {
        clearRender();
      }
    },
    clearAll() {
      diffs.clear();
      clearRender();
    },
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
