// Renders multi-line / doc comments as styled prose inside the live editor: each qualifying comment block's
// raw lines are collapsed (Monaco hidden areas) and replaced in place by a view-zone widget showing the
// comment as wrapped prose with numbered/bulleted lists and inline `code` chips (see comment-markup.ts for
// the parse). Clicking a rendered block reveals its raw text for editing; it re-renders once the caret leaves.
//
// The model is never mutated — only decorations/zones/hidden-areas, all owned here and torn down on dispose —
// so saving, diffing, and LSP all still see the real comment text. Suspended for a model that has an active
// inline diff (`isBlocked`) so collapsing never hides a changed line under review.

import { currentEditorOptions, onEditorOptionsChanged } from "../editor-options";
import { onFontsChanged } from "../fonts";
import {
  type CommentBlock,
  type Inline,
  type ProseBlock,
  commentSyntaxFor,
  parseProse,
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

// Build the prose DOM for a comment block, matching the editor's font metrics so it sits naturally in the gap
// the collapsed lines left behind. Returns an outer zone node (which Monaco force-sizes to the zone height)
// wrapping an inner content node whose natural height is what we measure — so the zone shrinks to the real
// wrapped height instead of the over-estimated seed.
function buildProseNode(
  editor: monaco.editor.IStandaloneCodeEditor,
  blocks: ProseBlock[],
): { outer: HTMLElement; inner: HTMLElement } {
  const outer = document.createElement("div");
  outer.className = "weavie-comment-prose-zone";
  const inner = document.createElement("div");
  inner.className = "weavie-comment-prose";
  outer.appendChild(inner);
  const fontInfo = editor.getOption(monaco.editor.EditorOption.fontInfo);
  inner.style.setProperty("--prose-font", fontInfo.fontFamily);

  const appendInline = (parent: HTMLElement, runs: Inline[]): void => {
    for (const run of runs) {
      if ("code" in run) {
        const code = document.createElement("code");
        code.className = "weavie-comment-code";
        code.textContent = run.code;
        parent.appendChild(code);
      } else {
        parent.appendChild(document.createTextNode(run.text));
      }
    }
  };

  for (const block of blocks) {
    if (block.kind === "p") {
      const p = document.createElement("p");
      appendInline(p, block.runs);
      inner.appendChild(p);
    } else {
      const list = document.createElement(block.kind === "ol" ? "ol" : "ul");
      if (block.kind === "ol" && block.start !== 1) {
        (list as HTMLOListElement).start = block.start;
      }
      for (const item of block.items) {
        const li = document.createElement("li");
        appendInline(li, item);
        list.appendChild(li);
      }
      inner.appendChild(list);
    }
  }
  return { outer, inner };
}

/** Creates a comment-prose controller bound to `editor`. */
export function createCommentProse(
  editor: monaco.editor.IStandaloneCodeEditor,
  deps: CommentProseDeps,
): CommentProse {
  const hiddenEditor = editor as unknown as HiddenAreasEditor;
  let enabled = currentEditorOptions().commentProse;
  let zoneIds: string[] = [];
  const observers: ResizeObserver[] = [];
  // The 1-based start line of the block the user expanded (clicked into) this tick — a transient hint so the
  // first render after a click keeps that block raw before the caret has moved into it.
  let pendingExpand: number | undefined;
  // The model line the caret was last on, so a cursor move only forces a re-render when it crosses into/out of
  // a different block (cheap on ordinary cursor movement within already-revealed code).
  let lastCursorBlock: number | undefined;
  // The blocks from the last render, reused by the cursor handler so an ordinary cursor move doesn't re-scan
  // the whole file (a content change re-scans via the debounced render and refreshes this).
  let cachedBlocks: CommentBlock[] = [];
  let rescanTimer: ReturnType<typeof setTimeout> | undefined;

  const clearRender = (): void => {
    for (const observer of observers) {
      observer.disconnect();
    }
    observers.length = 0;
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
  // holding the caret — because hidden areas stop the caret arrowing into a collapsed block, a block only ends
  // up containing the caret once the user has clicked it open.
  const blockAt = (blocks: CommentBlock[], line: number | undefined): CommentBlock | undefined =>
    line === undefined ? undefined : blocks.find((b) => line >= b.startLine && line <= b.endLine);

  const isFileModel = (model: monaco.editor.ITextModel | null): model is monaco.editor.ITextModel =>
    model !== null && model.uri.scheme === "file";

  const render = (): void => {
    clearRender();
    const model = editor.getModel();
    if (!enabled || !isFileModel(model) || deps.isBlocked(model.uri.toString())) {
      cachedBlocks = [];
      hiddenEditor.setHiddenAreas([], HIDDEN_AREAS_SOURCE);
      return;
    }

    const syntax = commentSyntaxFor(model.getLanguageId());
    const blocks = scanCommentBlocks(model.getLinesContent(), syntax);
    cachedBlocks = blocks;
    const caret = editor.getPosition()?.lineNumber;
    const caretBlock = blockAt(blocks, caret);

    const hidden: monaco.IRange[] = [];
    const pending: { zone: monaco.editor.IViewZone; inner: HTMLElement }[] = [];
    for (const block of blocks) {
      // Leave a block raw while it's being edited: the one the caret sits in, or the one just clicked open.
      if (block.startLine === pendingExpand || block === caretBlock) {
        continue;
      }
      const prose = parseProse(block.content, block.doc && syntax.xmlDoc === true);
      // Nothing but markers (e.g. a divider comment) — leave it as code rather than rendering an empty box.
      if (prose.length === 0) {
        continue;
      }
      const { outer, inner } = buildProseNode(editor, prose);
      attachClickToEdit(outer, block.startLine);
      hidden.push(new monaco.Range(block.startLine, 1, block.endLine, 1));
      const lineHeight = editor.getOption(monaco.editor.EditorOption.lineHeight);
      // Seed a non-zero height (one line per source line) so the zone reserves space before the ResizeObserver
      // measures the real wrapped height below.
      const zone: monaco.editor.IViewZone = {
        afterLineNumber: block.startLine - 1,
        heightInPx: Math.max(lineHeight, block.content.length * lineHeight),
        domNode: outer,
        suppressMouseDown: true,
        // The zone anchors at the edge of the collapsed (hidden) comment lines; without this Monaco treats it
        // as inside the hidden area and renders it at zero height (display:none).
        showInHiddenAreas: true,
      };
      pending.push({ zone, inner });
    }

    // Collapse the raw comment lines, then drop the prose widgets into the gaps they leave.
    hiddenEditor.setHiddenAreas(hidden, HIDDEN_AREAS_SOURCE);
    editor.changeViewZones((accessor) => {
      for (const { zone, inner } of pending) {
        const id = accessor.addZone(zone);
        zoneIds.push(id);
        // Size the zone to the prose's real height. Monaco force-sizes the zone's (outer) node to the zone
        // height, so we measure the INNER content node — whose height is natural — and shrink the zone to it
        // (wrapping depends on the editor width, so re-measure on resize). Guarded to only fire on a change.
        const observer = new ResizeObserver(() => {
          const height = inner.offsetHeight;
          if (height > 0 && height !== zone.heightInPx) {
            zone.heightInPx = height;
            editor.changeViewZones((acc) => acc.layoutZone(id));
          }
        });
        observer.observe(inner);
        observers.push(observer);
      }
    });

    lastCursorBlock = caretBlock?.startLine;
  };

  // Clicking the prose reveals the raw comment for editing: mark it pending-expand, re-render so its lines
  // un-hide and its widget drops away, then drop the caret onto the comment's first line.
  function attachClickToEdit(node: HTMLElement, startLine: number): void {
    node.addEventListener("mousedown", (event) => {
      event.preventDefault();
      pendingExpand = startLine;
      render();
      const model = editor.getModel();
      const column = (model?.getLineFirstNonWhitespaceColumn(startLine) ?? 1) || 1;
      editor.setPosition({ lineNumber: startLine, column });
      editor.revealLineInCenterIfOutsideViewport(startLine);
      editor.focus();
      pendingExpand = undefined;
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

  // A cursor move only matters when it crosses into (or out of) a comment block: re-render so the block the
  // caret just left re-collapses to prose and the one it entered stays raw. Reuses the last render's blocks
  // rather than re-scanning the file on every keystroke-driven cursor move (a content edit re-scans via the
  // debounced render).
  const onCursor = (): void => {
    const current = blockAt(cachedBlocks, editor.getPosition()?.lineNumber)?.startLine;
    if (current !== lastCursorBlock) {
      render();
    }
  };

  const subscriptions: monaco.IDisposable[] = [
    editor.onDidChangeModel(render),
    editor.onDidChangeModelContent(scheduleRescan),
    editor.onDidChangeCursorPosition(onCursor),
  ];
  const offFonts = onFontsChanged(render);
  const offOptions = onEditorOptionsChanged((options) => {
    if (options.commentProse !== enabled) {
      enabled = options.commentProse;
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
