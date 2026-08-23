// Renders multi-line / doc comments as styled prose in the live editor: each qualifying block's raw lines are
// collapsed (Monaco hidden areas) and replaced in place by a view-zone widget rendering the comment
// line-for-line (markers stripped, italic, inline `code` chips — see comment-markup.ts). Clicking or arrowing
// into a block reveals its raw text for editing; it re-renders once the caret leaves.
//
// Each rendered line is sized to exactly one editor line, so the zone matches the raw comment's footprint —
// collapse/expand is a zero-height swap, no measurement, the layout never shifts.
//
// The model is never mutated (only decorations/zones/hidden-areas, all torn down on dispose), so save/diff/LSP
// see the real text. Suspended for a model with an active inline diff (`isBlocked`) so collapsing never hides
// a changed line under review.

import type { CommentProseMode } from "../bridge";
import { currentEditorOptions, onEditorOptionsChanged } from "../editor-options";
import { onFontsChanged } from "../fonts";
import {
  type CommentBlock,
  commentSyntaxFor,
  type Inline,
  parseCommentLines,
  scanCommentBlocks,
  spanTouchesBlock,
} from "./comment-markup";
import { monaco } from "./monaco-setup";
import { SESSION_FILE_SCHEME } from "./session-uri";

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

// The pixel indent of a line's leading whitespace (tabs expanded to tab width), so a rendered comment sits at
// the same indentation as the code it documents rather than hugging the gutter.
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

// Whether a block renders under the active mode: `documentation` = doc comments only; `multiline` = docs plus
// any ≥2-line comment; `all` = every full-line comment. (`none` short-circuits before this is reached.)
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

// Build the prose DOM for a block: one `white-space: pre` node with line-height pinned to the editor's, source
// lines separated by real newlines, inline `code` lifted to chips and Markdown emphasis to styled spans,
// indented to `indentPx`. Pre + matched
// line-height makes the node's height deterministically `lines.length × lineHeight` (the raw footprint), no measurement.
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
      } else if (run.strong || run.em || run.strike) {
        const span = document.createElement("span");
        if (run.strong) span.classList.add("weavie-comment-strong");
        if (run.em) span.classList.add("weavie-comment-em");
        if (run.strike) span.classList.add("weavie-comment-strike");
        span.textContent = run.text;
        node.appendChild(span);
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
  // Start line of the block expanded this tick — a transient hint so the first render after a click keeps it raw.
  let pendingExpand: number | undefined;
  // The blocks the last render left raw, so a selection change only re-renders when that set changes.
  let lastRawKey = "";
  // The exact line the caret was last on, so the cursor handler can spot a single arrow step that a collapsed
  // block swallowed whole and pull the caret in, rather than letting one keypress skip the comment.
  let lastCursorLine: number | undefined;
  // True while we reposition the caret programmatically, so the re-entrant cursor event is ignored.
  let adjusting = false;
  // Blocks from the last render, reused by the cursor handler so an ordinary move doesn't re-scan the file.
  let cachedBlocks: CommentBlock[] = [];
  // Start lines of the blocks the last render actually collapsed, so only a genuinely hidden block can swallow
  // an arrow step (see crossedBlock) — a block the selection left raw is walked line by line like any code.
  let collapsedStarts = new Set<number>();
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

  // Start lines of the blocks any selection reaches into, in block order. Prose would hide the text the user
  // highlighted, so those blocks stay raw — an empty selection is the caret, so this also covers the block
  // being edited. Hidden areas keep the caret out of a collapsed block until it's opened (see onSelection).
  const selectedBlockStarts = (blocks: CommentBlock[]): number[] => {
    const selections = editor.getSelections() ?? [];
    return blocks
      .filter(
        (block) =>
          blockInMode(block, mode) &&
          selections.some((selection) => spanTouchesBlock(block, selection)),
      )
      .map((block) => block.startLine);
  };

  const isFileModel = (model: monaco.editor.ITextModel | null): model is monaco.editor.ITextModel =>
    model !== null && model.uri.scheme === SESSION_FILE_SCHEME;

  const render = (): void => {
    // Pin the scroll across the rebuild: clearRender drops every zone to zero height first, so Monaco
    // re-anchors the viewport mid-teardown. The rebuilt content is the same height, so restoring scrollTop
    // undoes the transient.
    const scrollTop = editor.getScrollTop();
    // The same teardown can momentarily size/position the hidden textarea Monaco uses for keyboard input out
    // from under the caret, dropping its focus (seen on CI: a click that extends a selection into a block —
    // the raw path below, not openBlockInline's own explicit refocus — landed correctly but left the editor
    // permanently unfocused, https://github.com/Kapps/weavie/actions/runs/32616592610/job/97139307788, 2026-08-23
    // 04:12 UTC). Restore it whenever the rebuild was the one that dropped it, never when focus already lived
    // elsewhere.
    const hadFocus = editor.hasTextFocus();
    clearRender();
    const model = editor.getModel();
    if (mode === "none" || !isFileModel(model) || deps.isBlocked(model.uri.toString())) {
      cachedBlocks = [];
      collapsedStarts = new Set();
      lastRawKey = "";
      hiddenEditor.setHiddenAreas([], HIDDEN_AREAS_SOURCE);
      editor.setScrollTop(scrollTop);
      if (hadFocus) {
        editor.focus();
      }
      return;
    }

    const syntax = commentSyntaxFor(model.getLanguageId());
    const blocks = scanCommentBlocks(model.getLinesContent(), syntax);
    cachedBlocks = blocks;
    const raw = selectedBlockStarts(blocks);

    collapsedStarts = new Set();
    const hidden: monaco.IRange[] = [];
    const lineHeight = editor.getOption(monaco.editor.EditorOption.lineHeight);
    const zones: monaco.editor.IViewZone[] = [];
    for (const block of blocks) {
      // Leave a block raw while the user is in it: any block a selection reaches into (prose would hide the
      // highlighted text), or the one just clicked open.
      if (block.startLine === pendingExpand || raw.includes(block.startLine)) {
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
      collapsedStarts.add(block.startLine);
      hidden.push(new monaco.Range(block.startLine, 1, block.endLine, 1));
      // The zone is exactly the raw comment's footprint (one editor-line-tall source line each, see
      // buildProseNode), so collapse/expand never reflows the code below.
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
    if (hadFocus) {
      editor.focus();
    }
    lastRawKey = raw.join(",");
  };

  // Reveal a collapsed block's raw text and land the caret on `caretLine` (first line when opened from the top,
  // last when arrowed up into from below). Un-hides it, then re-renders around the landed caret; scroll is
  // pinned to absorb any shift and `adjusting` swallows the re-entrant cursor event the setPosition fires.
  const openBlockInline = (startLine: number, caretLine: number): void => {
    const scrollTop = editor.getScrollTop();
    adjusting = true;
    pendingExpand = startLine;
    render();
    const model = editor.getModel();
    const column = (model?.getLineFirstNonWhitespaceColumn(caretLine) ?? 1) || 1;
    editor.setPosition({ lineNumber: caretLine, column });
    // Re-render with the caret inside: the block now holds itself raw, and blocks the previous selection was
    // holding open re-collapse. render() stays the only writer of lastRawKey, so it always describes the screen.
    pendingExpand = undefined;
    render();
    editor.setScrollTop(scrollTop);
    editor.focus();
    adjusting = false;
    lastCursorLine = caretLine;
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

  // The collapsed block the caret stepped ACROSS in one move (the move's endpoints bracket it), so the caret
  // can be pulled inside rather than skipping the whole comment. Only hidden lines can be stepped over, so a
  // block left raw by a selection is never pulled into — collapsing that selection just lands where it lands.
  const crossedBlock = (from: number, to: number): CommentBlock | undefined => {
    const collapsed = cachedBlocks.filter((b) => collapsedStarts.has(b.startLine));
    if (to > from + 1) {
      return collapsed.find((b) => b.startLine === from + 1 && b.endLine === to - 1);
    }
    if (to < from - 1) {
      return collapsed.find((b) => b.endLine === from - 1 && b.startLine === to + 1);
    }
    return undefined;
  };

  // A selection change matters two ways: (1) a single arrow step a collapsed block swallowed whole — open it on
  // its near edge so editing costs one keypress; (2) the set of blocks the selection touches changed —
  // re-render so the ones left re-collapse and the ones reached stay raw. Reuses the last render's blocks
  // rather than re-scanning.
  const onSelection = (): void => {
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
    if (selectedBlockStarts(cachedBlocks).join(",") !== lastRawKey) {
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
    // Selection rather than position: selecting to the caret's own side (e.g. select-all from the file's end)
    // moves no caret but still reaches into blocks.
    editor.onDidChangeCursorSelection(onSelection),
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
