import {
  createInlineDiff,
  type InlineDiff,
  type InlineDiffPresentation,
  type ReviewScopeState,
} from "../inline-diff";
import { createEmbeddedEditor, monaco } from "../monaco-setup";
import type { DiffMarkers } from "./diff-markers";
import type { ReviewFileDiff } from "./review-store";

const CONTEXT_LINES = 3;
const NOMINAL_LINE_HEIGHT = 19;
const EDITOR_PADDING = 12;
const HIDDEN_AREAS_SOURCE = "weavie.review";
type CollapsingEditor = monaco.editor.IStandaloneCodeEditor & {
  setHiddenAreas(ranges: monaco.IRange[], source: unknown): void;
};

export function estimatedEditorHeight(added: number, removed: number): number {
  return (added + removed + CONTEXT_LINES * 2) * NOMINAL_LINE_HEIGHT + EDITOR_PADDING;
}

export interface ReviewEditor {
  inline: InlineDiff;
  reveal(line: number): void;
  line(): number;
  update(diff: ReviewFileDiff): void;
  dispose(): void;
}

/** The section owns sizing and collapsed context; InlineDiff owns all review rendering and actions. */
export function createReviewEditor(options: {
  scope: ReviewScopeState;
  container: HTMLElement;
  model: monaco.editor.ITextModel;
  editable: boolean;
  diff: ReviewFileDiff;
  active: () => boolean;
  toolbarHost: () => HTMLElement | null;
  revealLine: (line: number, top: number) => void;
  viewport: () => { top: number; bottom: number } | null;
  configure: (inline: InlineDiff, uri: string, diff: ReviewFileDiff) => void;
  onHeight: () => void;
  onPainted: () => void;
  onCursor: (line: number) => void;
}): ReviewEditor {
  const { container, model } = options;
  const editor = createEmbeddedEditor(container, model, {
    readOnly: !options.editable,
    scrollBeyondLastLine: false,
    scrollbar: { alwaysConsumeMouseWheel: false, vertical: "hidden" },
    overviewRulerLanes: 0,
    overviewRulerBorder: false,
    hideCursorInOverviewRuler: true,
    minimap: { enabled: false },
    folding: false,
    stickyScroll: { enabled: false },
    renderLineHighlightOnlyWhenFocus: true,
    padding: { top: 6, bottom: 6 },
  }) as CollapsingEditor;
  const gaps = editor.createDecorationsCollection([]);
  let height = 0;
  let painted = false;
  const measure = (): void => {
    if (!painted) return;
    const next = editor.getContentHeight();
    if (height === next) return;
    height = next;
    container.style.height = `${next}px`;
    options.onHeight();
  };
  const presentation: InlineDiffPresentation = {
    scope: options.scope,
    parked: () => false,
    active: options.active,
    toolbarHost: options.toolbarHost,
    revealLine: (line) => options.revealLine(line, editor.getTopForLineNumber(line)),
    reviewLine: () => {
      const cursor = editor.getPosition()?.lineNumber ?? 1;
      const viewport = options.viewport();
      if (viewport === null) return cursor;
      const rect = container.getBoundingClientRect();
      const top = Math.max(viewport.top, rect.top) - rect.top;
      const bottom = Math.min(viewport.bottom, rect.bottom) - rect.top;
      const cursorTop = editor.getTopForLineNumber(cursor);
      if (cursorTop >= top && cursorTop < bottom) return cursor;
      const center = (top + bottom) / 2;
      let first = 1;
      let last = model.getLineCount();
      while (first < last) {
        const middle = Math.ceil((first + last) / 2);
        if (editor.getTopForLineNumber(middle) <= center) first = middle;
        else last = middle - 1;
      }
      return first;
    },
    painted: (markers) => {
      const collapsed = collapseUnchanged(markers, model.getLineCount());
      gaps.set(collapsed.gapMarkers);
      editor.setHiddenAreas(collapsed.hidden, HIDDEN_AREAS_SOURCE);
      painted = true;
      measure();
      options.onPainted();
    },
  };
  const inline = createInlineDiff(editor, presentation);
  const subscriptions = [
    editor.onDidContentSizeChange(measure),
    editor.onDidChangeCursorPosition((event) => options.onCursor(event.position.lineNumber)),
  ];
  options.configure(inline, model.uri.toString(), options.diff);
  return {
    inline,
    line: () => editor.getPosition()?.lineNumber ?? 1,
    reveal: (line) => {
      editor.setPosition({ lineNumber: line, column: 1 });
      presentation.revealLine(line);
    },
    update: (diff) => options.configure(inline, model.uri.toString(), diff),
    dispose: () => {
      for (const subscription of subscriptions) subscription.dispose();
      inline.dispose();
      gaps.clear();
      editor.dispose();
    },
  };
}

function collapseUnchanged(
  markers: DiffMarkers | null,
  lineCount: number,
): { hidden: monaco.IRange[]; gapMarkers: monaco.editor.IModelDeltaDecoration[] } {
  const spans = markers === null ? [] : changedSpans(markers);
  if (spans.length === 0) {
    return { hidden: [], gapMarkers: [] };
  }
  // A pure deletion's span is empty (end < start) and its ghost hangs off the line above, so pad both edges.
  const padded = spans
    .map((span) => ({
      start: Math.max(1, Math.min(span.start, span.end + 1) - CONTEXT_LINES),
      end: Math.min(lineCount, Math.max(span.end, span.start - 1) + CONTEXT_LINES),
    }))
    .sort((a, b) => a.start - b.start);
  const shown: { start: number; end: number }[] = [];
  for (const span of padded) {
    const last = shown.at(-1);
    if (last !== undefined && span.start <= last.end + 1) {
      last.end = Math.max(last.end, span.end);
    } else {
      shown.push({ ...span });
    }
  }

  const hidden: monaco.IRange[] = [];
  const gapMarkers: monaco.editor.IModelDeltaDecoration[] = [];
  let line = 1;
  for (const span of shown) {
    if (span.start > line) {
      hidden.push(new monaco.Range(line, 1, span.start - 1, 1));
      gapMarkers.push({
        range: new monaco.Range(span.start, 1, span.start, 1),
        options: { isWholeLine: true, className: "weavie-review-gap" },
      });
    }
    line = span.end + 1;
  }
  if (line <= lineCount) {
    hidden.push(new monaco.Range(line, 1, lineCount, 1));
  }
  return { hidden, gapMarkers };
}

// Every changed line range in live-model coordinates: the bright pending hunks plus the faded accepted ones.
function changedSpans(markers: DiffMarkers): { start: number; end: number }[] {
  return [
    ...markers.hunks.map((hunk) => ({
      start: hunk.currentStart,
      end: hunk.currentEndExclusive - 1,
    })),
    ...markers.acceptedHunks.map((hunk) => ({
      start: hunk.anchorLine,
      end: hunk.anchorLine + (hunk.reviewEndExclusive - hunk.reviewStart) - 1,
    })),
  ];
}
