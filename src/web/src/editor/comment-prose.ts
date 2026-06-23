// Renders multi-line / doc comments as styled prose inside the live editor: each qualifying comment block's
// raw lines are collapsed (Monaco hidden areas) and replaced in place by a view-zone widget showing the
// comment line-for-line — markers stripped, italic, with inline `code` chips (see comment-markup.ts for the
// parse) — preserving the author's line breaks exactly. Clicking a rendered block, or arrowing into it,
// reveals its raw text for editing; it re-renders once the caret leaves.
//
// Each rendered line is sized to exactly one editor line, so the zone occupies the SAME footprint as the raw
// comment it replaces. Collapse/expand is a zero-height swap with no measurement — the layout never shifts.
//
// The model is never mutated — only decorations/zones/hidden-areas, all owned here and torn down on dispose —
// so saving, diffing, and LSP all still see the real comment text. Suspended for a model that has an active
// inline diff (`isBlocked`) so collapsing never hides a changed line under review.

import type { CommentProseMode } from "../bridge";
import { currentEditorOptions, onEditorOptionsChanged } from "../editor-options";
import { onFontsChanged } from "../fonts";
import {
  type CommentBlock,
  type Inline,
  commentSyntaxFor,
  parseCommentLines,
  scanCommentBlocks,
} from "./comment-markup";
import { monaco } from "./monaco-setup";

// Re-scan after edits settle rather than on every keystroke (matches inline-diff's recompute debounce).
const RESCAN_DEBOUNCE_MS = 150;

// A private source token for setHiddenAreas so our collapsed comments coexist with the folding controller's
// own hidden areas instead of clobbering them. setHiddenAreas isn't on the public IStandaloneCodeEditor type.
const HIDDEN_AREAS_SOURCE = "weavie.commentProse";
interface HiddenAreasEditor {
  setHiddenAreas(ranges: monaco.IRange[], source: unknown): void;
}

export interface CommentProseDeps {
  /** True when the model at `uri` has an active inline diff: suspend rendering so no changed line is hidden. */
  isBlocked: (uri: string) => boolean;
}

export interface CommentProse {
  /** Re-render the active model (call after anything that changes `isBlocked`, e.g. a diff appearing/clearing). */
  refresh(): void;
  /** Tear down listeners, zones, and hidden areas (never mutates the model). */
  dispose(): void;
}

// The pixel indent of a model line's leading whitespace (tabs expanded to the model's tab width) so a rendered
// comment sits at the same indentation as the raw comment — and the code — it documents, instead of hugging
// the gutter regardless of nesting.
function indentPixelsFor(
  editor: monaco.editor.IStandaloneCodeEditor,
  model: monaco.editor.ITextModel,
  line: number,
): number {
  const text = model.getLineContent(line);
  const wsLen = text.length - text.trimStart().length;
  if (wsLen === 0) {
    return 0;
  }
  const tabSize = model.getOptions().tabSize;
  let columns = 0;
  for (let k = 0; k < wsLen; k++) {
    columns += text[k] === "\t" ? tabSize - (columns % tabSize) : 1;
  }
  return columns * editor.getOption(monaco.editor.EditorOption.fontInfo).spaceWidth;
}

// True when a parsed comment carries any visible text/code — not just stripped markers or blank lines (e.g. a
// divider comment), which we leave as raw code rather than rendering an empty box.
function hasVisibleContent(lines: Inline[][]): boolean {
  return lines.some((runs) =>
    runs.some((run) => ("code" in run ? run.code : run.text).trim() !== ""),
  );
}

// Whether a block is rendered under the active mode. `documentation`: doc comments only (incl. single-line
// ones). `multiline`: doc comments plus any comment spanning ≥2 lines. `all`: every full-line comment. (`none`
// short-circuits the whole render before this is reached.)
function blockInMode(block: CommentBlock, mode: CommentProseMode): boolean {
  switch (mode) {
    case "all":
      return true;
    case "multiline":
      return block.doc || block.endLine > block.startLine;
    default:
      return block.doc;
  }
}

// Build the prose DOM for a comment block: one `white-space: pre` node whose line-height is pinned to the
// editor's, with the source lines separated by real newlines and inline `code` spans lifted to chips. Pre +
// the matched line-height makes each source line exactly one editor line tall (and never wraps), so the node's
// height is deterministically `lines.length × lineHeight` — the raw comment's footprint — with no measurement.
// Indented to `indentPx` to track the comment's nesting.
function buildProseNode(
  editor: monaco.editor.IStandaloneCodeEditor,
  lines: Inline[][],
  indentPx: number,
): HTMLElement {
  const node = document.createElement("div");
  node.className = "weavie-comment-prose";
  const fontInfo = editor.getOption(monaco.editor.EditorOption.fontInfo);
  const lineHeight = editor.getOption(monaco.editor.EditorOption.lineHeight);
  node.style.setProperty("--prose-font", fontInfo.fontFamily);
  node.style.setProperty("--prose-indent", `${indentPx}px`);
  node.style.setProperty("--prose-line", `${lineHeight}px`);

  lines.forEach((runs, index) => {
    // A real newline between source lines; `white-space: pre` renders each as its own (non-wrapping) line.
    if (index > 0) {
      node.appendChild(document.createTextNode("\n"));
    }
    for (const run of runs) {
      if ("code" in run) {
        const code = document.createElement("code");
        code.className = "weavie-comment-code";
        code.textContent = run.code;
        node.appendChild(code);
      } else {
        node.appendChild(document.createTextNode(run.text));
      }
    }
  });
  return node;
}

/** Creates a comment-prose controller bound to `editor`. */
export function createCommentProse(
  editor: monaco.editor.IStandaloneCodeEditor,
  deps: CommentProseDeps,
): CommentProse {
  const hiddenEditor = editor as unknown as HiddenAreasEditor;
  let mode = currentEditorOptions().commentProse;
  let zoneIds: string[] = [];
  // The 1-based start line of the block the user expanded (clicked into) this tick — a transient hint so the
  // first render after a click keeps that block raw before the caret has moved into it.
  let pendingExpand: number | undefined;
  // The model line the caret was last on, so a cursor move only forces a re-render when it crosses into/out of
  // a different block (cheap on ordinary cursor movement within already-revealed code).
  let lastCursorBlock: number | undefined;
  // The exact model line the caret was last on, so the cursor handler can spot a single arrow step that a
  // collapsed block swallowed whole (line-before -> line-after in one move) and pull the caret in, instead of
  // letting one keypress skip the entire comment.
  let lastCursorLine: number | undefined;
  // True while we reposition the caret programmatically (opening a block): the re-entrant cursor event our own
  // setPosition fires is ignored rather than re-triggering the open.
  let adjusting = false;
  // The blocks from the last render, reused by the cursor handler so an ordinary cursor move doesn't re-scan
  // the whole file (a content change re-scans via the debounced render and refreshes this).
  let cachedBlocks: CommentBlock[] = [];
  let rescanTimer: ReturnType<typeof setTimeout> | undefined;

  const clearRender = (): void => {
    if (zoneIds.length > 0) {
      editor.changeViewZones((accessor) => {
        for (const id of zoneIds) {
          accessor.removeZone(id);
        }
      });
      zoneIds = [];
    }
  };

  // The block (if any) whose line range contains `line`. A revealed (expanded) block is exactly the one
  // holding the caret — hidden areas stop the caret drifting into a collapsed block, so it only contains the
  // caret once opened (by a click on its prose, or by arrowing into it — see onCursor).
  const blockAt = (blocks: CommentBlock[], line: number | undefined): CommentBlock | undefined =>
    line === undefined ? undefined : blocks.find((b) => line >= b.startLine && line <= b.endLine);

  const isFileModel = (model: monaco.editor.ITextModel | null): model is monaco.editor.ITextModel =>
    model !== null && model.uri.scheme === "file";

  const render = (): void => {
    // Pin the scroll across the whole rebuild. clearRender tears down every zone before the new ones go in, so
    // each collapsed comment briefly drops to zero height and Monaco re-anchors the viewport mid-teardown —
    // with several collapsed comments above you, that lands the scroll somewhere else (a big jump when a render
    // fires from clicking/arrowing out of a block). The rebuilt content is the same height (zones match the raw
    // lines), so restoring the pre-render scroll just undoes the transient.
    const scrollTop = editor.getScrollTop();
    clearRender();
    const model = editor.getModel();
    if (mode === "none" || !isFileModel(model) || deps.isBlocked(model.uri.toString())) {
      cachedBlocks = [];
      hiddenEditor.setHiddenAreas([], HIDDEN_AREAS_SOURCE);
      editor.setScrollTop(scrollTop);
      return;
    }

    const syntax = commentSyntaxFor(model.getLanguageId());
    const blocks = scanCommentBlocks(model.getLinesContent(), syntax);
    cachedBlocks = blocks;
    const caret = editor.getPosition()?.lineNumber;
    const caretBlock = blockAt(blocks, caret);

    const hidden: monaco.IRange[] = [];
    const lineHeight = editor.getOption(monaco.editor.EditorOption.lineHeight);
    const zones: monaco.editor.IViewZone[] = [];
    for (const block of blocks) {
      // Leave a block raw while it's being edited: the one the caret sits in, or the one just clicked open.
      if (block.startLine === pendingExpand || block === caretBlock) {
        continue;
      }
      // Leave blocks the active mode doesn't cover as plain code.
      if (!blockInMode(block, mode)) {
        continue;
      }
      const lines = parseCommentLines(block.content, block.doc && syntax.xmlDoc === true);
      if (!hasVisibleContent(lines)) {
        continue;
      }
      const node = buildProseNode(editor, lines, indentPixelsFor(editor, model, block.startLine));
      attachClickToEdit(node, block.startLine);
      hidden.push(new monaco.Range(block.startLine, 1, block.endLine, 1));
      // The zone is exactly the raw comment's footprint: one source line per line, each sized to the editor's
      // line height (see buildProseNode). Collapsing to prose never reflows the code below, and expanding back
      // to raw is a zero-height swap — no measurement, no grow, the layout never shifts.
      zones.push({
        afterLineNumber: block.startLine - 1,
        heightInPx: block.content.length * lineHeight,
        domNode: node,
        suppressMouseDown: true,
        // The zone anchors at the edge of the collapsed (hidden) comment lines; without this Monaco treats it
        // as inside the hidden area and renders it at zero height (display:none).
        showInHiddenAreas: true,
      });
    }

    // Collapse the raw comment lines, then drop the prose widgets into the gaps they leave.
    hiddenEditor.setHiddenAreas(hidden, HIDDEN_AREAS_SOURCE);
    editor.changeViewZones((accessor) => {
      for (const zone of zones) {
        zoneIds.push(accessor.addZone(zone));
      }
    });

    // Undo any scroll Monaco shifted while zones were torn down and rebuilt above (see the pin at the top).
    editor.setScrollTop(scrollTop);
    lastCursorBlock = caretBlock?.startLine;
  };

  // Reveal a collapsed block's raw text for editing and land the caret on `caretLine` (its first line when
  // opened from the top — a click or an arrow-down into it — its last line when arrowed up into from below).
  // Re-renders so the block un-hides and its prose widget drops away; because the zone kept the raw comment's
  // footprint, nothing below moves, and we pin the scroll to absorb any incidental shift. `adjusting` swallows
  // the re-entrant cursor event the setPosition fires so it doesn't re-trigger this.
  const openBlockInline = (startLine: number, caretLine: number): void => {
    const scrollTop = editor.getScrollTop();
    adjusting = true;
    pendingExpand = startLine;
    render();
    const model = editor.getModel();
    const column = (model?.getLineFirstNonWhitespaceColumn(caretLine) ?? 1) || 1;
    editor.setPosition({ lineNumber: caretLine, column });
    editor.setScrollTop(scrollTop);
    editor.focus();
    pendingExpand = undefined;
    adjusting = false;
    lastCursorLine = caretLine;
    lastCursorBlock = startLine;
  };

  // Clicking the prose opens the block for editing with the caret on its first line.
  function attachClickToEdit(node: HTMLElement, startLine: number): void {
    node.addEventListener("mousedown", (event) => {
      event.preventDefault();
      openBlockInline(startLine, startLine);
    });
  }

  const scheduleRescan = (): void => {
    if (rescanTimer !== undefined) {
      clearTimeout(rescanTimer);
    }
    rescanTimer = setTimeout(() => {
      rescanTimer = undefined;
      render();
    }, RESCAN_DEBOUNCE_MS);
  };

  // The collapsed block the caret just stepped ACROSS in a single move: its line-before and line-after are
  // exactly the move's endpoints, i.e. one arrow press the hidden area swallowed whole. Returns it so the
  // caret can be pulled inside rather than skipping the entire comment.
  const crossedBlock = (from: number, to: number): CommentBlock | undefined => {
    if (to > from + 1) {
      return cachedBlocks.find((b) => b.startLine === from + 1 && b.endLine === to - 1);
    }
    if (to < from - 1) {
      return cachedBlocks.find((b) => b.endLine === from - 1 && b.startLine === to + 1);
    }
    return undefined;
  };

  // A cursor move matters two ways: (1) a single arrow step a collapsed block swallowed whole — open it and
  // land the caret on its near edge, so editing one comment costs one keypress, not a multi-line skip; (2)
  // crossing into/out of a block's range — re-render so the block just left re-collapses to prose and the one
  // entered stays raw. Reuses the last render's blocks rather than re-scanning on every keystroke-driven move.
  const onCursor = (): void => {
    if (adjusting) {
      return;
    }
    const line = editor.getPosition()?.lineNumber;
    const prev = lastCursorLine;
    lastCursorLine = line;
    // Only a plain caret move pulls in (an empty selection); a shift-select across a block extends normally.
    if (line !== undefined && prev !== undefined && editor.getSelection()?.isEmpty() === true) {
      const crossed = crossedBlock(prev, line);
      if (crossed !== undefined) {
        openBlockInline(crossed.startLine, line > prev ? crossed.startLine : crossed.endLine);
        return;
      }
    }
    const current = blockAt(cachedBlocks, line)?.startLine;
    if (current !== lastCursorBlock) {
      render();
    }
  };

  const subscriptions: monaco.IDisposable[] = [
    // Drop the remembered caret line on a model switch so a stale line from the previous file can't be read as
    // an arrow step across a block in the new one.
    editor.onDidChangeModel(() => {
      lastCursorLine = undefined;
      render();
    }),
    editor.onDidChangeModelContent(scheduleRescan),
    editor.onDidChangeCursorPosition(onCursor),
  ];
  const offFonts = onFontsChanged(render);
  const offOptions = onEditorOptionsChanged((options) => {
    if (options.commentProse !== mode) {
      mode = options.commentProse;
      render();
    }
  });

  render();

  return {
    refresh: render,
    dispose: () => {
      if (rescanTimer !== undefined) {
        clearTimeout(rescanTimer);
      }
      for (const subscription of subscriptions) {
        subscription.dispose();
      }
      offFonts();
      offOptions();
      clearRender();
      hiddenEditor.setHiddenAreas([], HIDDEN_AREAS_SOURCE);
    },
  };
}
