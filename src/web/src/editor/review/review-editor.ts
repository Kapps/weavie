// One changed file's diff in the unified review, rendered by a real Monaco editor bound to that file's live
// working copy — so it carries the same syntax + semantic highlighting, LSP hovers/diagnostics/go-to-definition,
// completions and editing as the file-review pane. Unchanged regions are collapsed into hidden areas so the
// section reads as a diff instead of a whole file, and the editor is sized to its content so the review's own
// list does all the scrolling.

import { createEmbeddedEditor, monaco } from "../monaco-setup";
import { computeDiffMarkers, type DiffMarkers } from "./diff-markers";
import { addDiffZones, DIFF_RECOMPUTE_DEBOUNCE_MS } from "./diff-zones";
import type { ReviewFileDiff } from "./review-store";

// Unchanged lines kept either side of a change, matching the file-review reading distance.
const CONTEXT_LINES = 3;

// A section reserves its height from its line counts until the real editor reports one, so the virtualizer's
// offsets don't collapse while models resolve. Nominal metrics — the editor overwrites them on first measure.
const NOMINAL_LINE_HEIGHT = 19;
const EDITOR_PADDING = 12;

// setHiddenAreas is how VS Code's own diff editor collapses unchanged regions. It lives on the CodeEditorWidget
// every standalone editor is, but isn't part of Monaco's published standalone typings. A private source token
// keeps our collapsed regions from clobbering another contributor's, as comment-prose.ts does.
const HIDDEN_AREAS_SOURCE = "weavie.review";
type CollapsingEditor = monaco.editor.IStandaloneCodeEditor & {
  setHiddenAreas(ranges: monaco.IRange[], source: unknown): void;
};

const EDITOR_OPTIONS: monaco.editor.IEditorOptions = {
  // The section is sized to its content, so the review list owns all scrolling: no internal scrollbar, and the
  // wheel passes straight through to the list.
  scrollBeyondLastLine: false,
  scrollbar: { alwaysConsumeMouseWheel: false, vertical: "hidden" },
  overviewRulerLanes: 0,
  overviewRulerBorder: false,
  hideCursorInOverviewRuler: true,
  minimap: { enabled: false },
  // Folding and sticky scroll drive hidden areas themselves and would replace the collapsed regions below.
  folding: false,
  stickyScroll: { enabled: false },
  renderLineHighlightOnlyWhenFocus: true,
  padding: { top: 6, bottom: 6 },
};

/** The height a section's editor reserves before it mounts: its changed lines plus the context around them. */
export function estimatedEditorHeight(added: number, removed: number): number {
  return (added + removed + CONTEXT_LINES * 2) * NOMINAL_LINE_HEIGHT + EDITOR_PADDING;
}

/** A file's live diff editor. `update` repaints it from a fresh host push; `dispose` tears it down. */
export interface ReviewEditor {
  update(diff: ReviewFileDiff): void;
  dispose(): void;
}

/**
 * Mounts the diff editor for one review file in `container`, bound to `model` (the file's working copy).
 * `onHeight` fires whenever the rendered height changes, so the caller can re-measure its virtualized row;
 * `onTimedOut` reports whether the diff engine gave up, leaving the file shown plain and uncollapsed.
 */
export function createReviewEditor(options: {
  container: HTMLElement;
  model: monaco.editor.ITextModel;
  diff: ReviewFileDiff;
  onHeight: () => void;
  onTimedOut: (timedOut: boolean) => void;
}): ReviewEditor {
  const { container, model } = options;
  const editor = createEmbeddedEditor(container, model, EDITOR_OPTIONS) as CollapsingEditor;
  const decorations = editor.createDecorationsCollection([]);
  let zoneIds: string[] = [];
  let current = options.diff;
  let height = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;

  const paint = (): void => {
    const markers = computeDiffMarkers(
      {
        original: current.baseline,
        acceptedBaseline: current.acceptedBaseline,
        claudeVersion: current.current,
      },
      model.getLinesContent(),
    );
    editor.changeViewZones((accessor) => {
      for (const id of zoneIds) {
        accessor.removeZone(id);
      }
      zoneIds = markers === null ? [] : addDiffZones(editor, accessor, markers);
    });
    const collapsed = collapseUnchanged(markers, model.getLineCount());
    decorations.set([...(markers?.decorations ?? []), ...collapsed.gapMarkers]);
    editor.setHiddenAreas(collapsed.hidden, HIDDEN_AREAS_SOURCE);
    options.onTimedOut(markers === null);
  };

  const repaint = (): void => {
    if (timer !== undefined) {
      clearTimeout(timer);
    }
    timer = setTimeout(paint, DIFF_RECOMPUTE_DEBOUNCE_MS);
  };

  const measure = (): void => {
    const next = editor.getContentHeight();
    if (next === height) {
      return;
    }
    height = next;
    container.style.height = `${next}px`;
    options.onHeight();
  };

  const subscriptions = [model.onDidChangeContent(repaint), editor.onDidContentSizeChange(measure)];
  paint();
  measure();

  return {
    update: (next: ReviewFileDiff) => {
      current = next;
      paint();
    },
    dispose: () => {
      if (timer !== undefined) {
        clearTimeout(timer);
      }
      for (const subscription of subscriptions) {
        subscription.dispose();
      }
      // The model belongs to the editor host's review-copy pool — drop the widget only, never the model.
      editor.setModel(null);
      editor.dispose();
    },
  };
}

/**
 * Hides every line more than `CONTEXT_LINES` from a change (bright or accepted), and marks the first line after
 * each collapsed stretch so a gap reads as a gap. A timed-out diff (null markers) collapses nothing.
 */
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
