// Renders a diff INSIDE the live code editor (Cursor-style), never a standalone diff viewer: added lines as
// green whole-line decorations, removed lines as red "ghost" view-zones in place, char-level highlights for
// replacements, and a small Accept/Reject(/Undo) toolbar. The modified side is always the editor's LIVE model
// content, so the diff tracks edits live (the user tweaking a proposal, or the working copy reloading after a
// Claude edit). The layer owns only its decorations/zones/widget — it NEVER disposes the host-owned live model.

import { linesDiffComputers } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/diff/linesDiffComputers";
import { formatKey } from "../commands/keybindings";
import { findCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { onFontsChanged } from "../fonts";
import { canonicalFsPath } from "./fs-path";
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
   * The content Claude produced — the on-disk version for an applied turn, the proposal for a review. Lines in
   * the live model that differ from THIS are the user's own typing (not Claude's), and render in a fainter
   * green so a person's edits read as distinct from Claude's pending changes (which the diff is for reviewing).
   * Right after a reload the model equals this, so nothing reads as "user" until you type. Omitted → no fade
   * (every changed line is treated as Claude's).
   */
  claudeVersion?: string;
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

/**
 * The modified-side line (1-based) of the FIRST change between `original` and `modified`, or 1 when they're
 * identical. Used to reveal an openDiff review at its first hunk rather than the top of the file. Computed with
 * the same diff machinery (and anchor rule) `render` uses for change navigation, so "reveal first change" and
 * the first "next change" land on the same line.
 */
export function firstChangedLine(original: string, modified: string): number {
  const { changes } = linesDiffComputers
    .getDefault()
    .computeDiff(splitLines(original), splitLines(modified), DIFF_OPTIONS);
  const first = changes[0];
  return first === undefined ? 1 : Math.max(1, first.modified.startLineNumber);
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
    // Match the editor's RESOLVED metrics, not the raw font setting: a view zone reserves exactly
    // `lines.length * fontInfo.lineHeight` px, so the ghost rows must use that same line height (which
    // Monaco derives from the font size — the editor never sets one explicitly). Inheriting the chrome's
    // line-height instead made N rows overflow the N-line zone and overlap the code above/below.
    const fontInfo = editor.getOption(monaco.editor.EditorOption.fontInfo);
    node.style.fontFamily = fontInfo.fontFamily;
    node.style.fontSize = `${fontInfo.fontSize}px`;
    node.style.lineHeight = `${fontInfo.lineHeight}px`;
    // Render tabs at the editor's tab width (CSS `tab-size` defaults to 8) so a removed line's leading
    // indentation lines up with the live code above/below it instead of being doubled.
    node.style.tabSize = String(editor.getModel()?.getOptions().tabSize ?? 4);
    for (const line of lines) {
      const row = document.createElement("div");
      row.className = "weavie-inline-removed-line";
      row.textContent = line.length === 0 ? " " : line;
      node.appendChild(row);
    }
    return node;
  };

  const makeButton = (
    className: string,
    label: string,
    title: string,
    onClick: () => void,
  ): HTMLButtonElement => {
    const button = document.createElement("button");
    button.type = "button";
    button.className = className;
    button.textContent = label;
    button.title = title;
    button.addEventListener("click", () => onClick());
    return button;
  };

  // Weavie nudges users toward the keyboard, so every toolbar button advertises its shortcut on hover:
  // "<label> (<shortcut>)", using the command's currently-bound keys (defaults merged with the user's
  // keybindings.json). Unbound commands (e.g. Undo, which ships without a default binding) show just the
  // label. Falls back to the bare label in plain-browser dev where the host hasn't injected a catalog.
  const withShortcut = (label: string, commandId: string): string => {
    const keys = findCommand(commandId)?.keys ?? [];
    return keys.length > 0 ? `${label} (${keys.map(formatKey).join(" / ")})` : label;
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

    const prev = makeButton(
      "weavie-inline-nav",
      "↑",
      withShortcut("Previous change", CommandIds.prevChange),
      prevChange,
    );
    const next = makeButton(
      "weavie-inline-nav",
      "↓",
      withShortcut("Next change", CommandIds.nextChange),
      nextChange,
    );
    bar.append(prev, next);

    if (options.mode === "review") {
      if (options.onAccept !== undefined) {
        bar.appendChild(
          makeButton(
            "weavie-inline-accept",
            "Keep",
            withShortcut("Keep this change", CommandIds.acceptChange),
            accept,
          ),
        );
      }
      if (options.onReject !== undefined) {
        bar.appendChild(
          makeButton(
            "weavie-inline-reject",
            "Reject",
            withShortcut("Reject this change", CommandIds.rejectChange),
            reject,
          ),
        );
      }
    } else if (options.mode === "applied") {
      if (options.onAccept !== undefined) {
        bar.appendChild(
          makeButton(
            "weavie-inline-accept",
            "Accept",
            withShortcut("Accept these changes", CommandIds.acceptChange),
            accept,
          ),
        );
      }
      if (options.onUndo !== undefined) {
        bar.appendChild(
          makeButton(
            "weavie-inline-undo",
            "Undo",
            withShortcut("Undo these changes", CommandIds.undoChange),
            undo,
          ),
        );
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

    // Lines the USER typed render fainter than Claude's: diff the live model against `claudeVersion` (what
    // Claude produced), and any line that differs is the person's own edit. Empty when claudeVersion is
    // omitted or the model still matches it (just-reloaded), so it only lights up as you type over a change.
    const userLines = new Set<number>();
    if (options.claudeVersion !== undefined) {
      const userDiff = linesDiffComputers
        .getDefault()
        .computeDiff(splitLines(options.claudeVersion), modified, DIFF_OPTIONS);
      for (const change of userDiff.changes) {
        for (
          let ln = change.modified.startLineNumber;
          ln < change.modified.endLineNumberExclusive;
          ln++
        ) {
          userLines.add(ln);
        }
      }
    }

    const deltas: monaco.editor.IModelDeltaDecoration[] = [];
    const ghosts: { afterLineNumber: number; lines: string[] }[] = [];
    const changeLines: number[] = [];

    for (const change of changes) {
      changeLines.push(Math.max(1, change.modified.startLineNumber));
      if (!change.modified.isEmpty) {
        // Per-line (not one whole-range decoration) so a block that mixes Claude's lines with the user's
        // tweaks paints each in its own shade.
        for (
          let ln = change.modified.startLineNumber;
          ln < change.modified.endLineNumberExclusive;
          ln++
        ) {
          const fromUser = userLines.has(ln);
          deltas.push({
            range: new monaco.Range(ln, 1, ln, 1),
            options: {
              isWholeLine: true,
              className: fromUser ? "weavie-inline-user" : "weavie-inline-added",
              linesDecorationsClassName: fromUser
                ? "weavie-inline-user-gutter"
                : "weavie-inline-added-gutter",
              overviewRuler: {
                color: fromUser ? "rgba(78, 201, 120, 0.3)" : "rgba(78, 201, 120, 0.7)",
                position: monaco.editor.OverviewRulerLane.Left,
              },
            },
          });
        }
        for (const inner of change.innerChanges ?? []) {
          const r = inner.modifiedRange;
          // Char-level emphasis is for reviewing Claude's edits; skip it on the user's own (faint) lines.
          if (userLines.has(r.startLineNumber)) {
            continue;
          }
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
      setByUri(monaco.Uri.file(canonicalFsPath(path)).toString(), options);
    },
    clear(path) {
      clearByUri(monaco.Uri.file(canonicalFsPath(path)).toString());
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
