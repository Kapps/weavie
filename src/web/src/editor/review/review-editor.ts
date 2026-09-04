// One changed file's diff in the unified review, rendered by a real Monaco editor bound to that file's live
// working copy — so it carries the same syntax + semantic highlighting, LSP hovers/diagnostics/go-to-definition,
// completions and editing as the file-review pane. Unchanged regions are collapsed into hidden areas so the
// section reads as a diff instead of a whole file, and the editor is sized to its content so the review's own
// list does all the scrolling.

import { log } from "../../bridge";
import { createEmbeddedEditor, monaco } from "../monaco-setup";
import { DiffComputer } from "./diff-computer";
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
  /** Whether the diff geometry has landed — until it has, `changeLines` can't answer for this file yet. */
  painted(): boolean;
  /** The anchor line of every change still pending review, in document order — the spots the walk stops at. */
  changeLines(): number[];
  /** `line`'s offset from the top of this editor, so the review's own list can scroll the spot into view. */
  topForLine(line: number): number;
}

/**
 * Mounts the diff editor for one review file in `container`, bound to `model` (the file's working copy).
 * `onHeight` fires whenever the rendered height changes, so the caller can re-measure its virtualized row;
 * `onPainted` whenever the diff geometry lands, so a queued walk can reveal a spot it couldn't resolve yet;
 * `onStatus` reports whether the diff is ready or unavailable, leaving an unavailable file plain and uncollapsed.
 */
export function createReviewEditor(options: {
  container: HTMLElement;
  model: monaco.editor.ITextModel;
  editable: boolean;
  diff: ReviewFileDiff;
  onHeight: () => void;
  onPainted: () => void;
  onStatus: (status: "ready" | "timed-out" | "failed") => void;
}): ReviewEditor {
  const { container, model } = options;
  const editor = createEmbeddedEditor(container, model, {
    ...EDITOR_OPTIONS,
    readOnly: !options.editable,
  }) as CollapsingEditor;
  const computer = new DiffComputer();
  const decorations = editor.createDecorationsCollection([]);
  let zoneIds: string[] = [];
  let current = options.diff;
  let height = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let renderGeneration = 0;
  let renderQueued = false;
  let renderInFlight = false;
  let painted = false;
  let markers: DiffMarkers | null = null;
  let disposed = false;

  const measure = (): void => {
    if (!painted) {
      return;
    }
    const next = editor.getContentHeight();
    if (next === height) {
      return;
    }
    height = next;
    container.style.height = `${next}px`;
    options.onHeight();
  };

  const applyMarkers = (next: DiffMarkers | null): void => {
    markers = next;
    editor.changeViewZones((accessor) => {
      for (const id of zoneIds) {
        accessor.removeZone(id);
      }
      zoneIds = next === null ? [] : addDiffZones(editor, accessor, next);
    });
    const collapsed = collapseUnchanged(next, model.getLineCount());
    decorations.set([...(next?.decorations ?? []), ...collapsed.gapMarkers]);
    editor.setHiddenAreas(collapsed.hidden, HIDDEN_AREAS_SOURCE);
    painted = true;
    measure();
    options.onPainted();
  };

  const render = async (generation: number, diff: ReviewFileDiff): Promise<void> => {
    const version = model.getVersionId();
    const calculation = await computer.compute(
      model.uri.toString(),
      {
        original: diff.baseline,
        acceptedBaseline: diff.acceptedBaseline,
        claudeVersion: diff.current,
      },
      model,
    );
    if (
      disposed ||
      generation !== renderGeneration ||
      current !== diff ||
      model.getVersionId() !== version
    ) {
      return;
    }
    if (calculation.status !== "ready") {
      if (calculation.status === "failed") {
        log("error", `unified review diff calculation failed: ${String(calculation.error)}`);
      }
      applyMarkers(null);
      options.onStatus(calculation.status);
      return;
    }
    applyMarkers(
      computeDiffMarkers(
        {
          original: diff.baseline,
          acceptedBaseline: diff.acceptedBaseline,
          claudeVersion: diff.current,
        },
        calculation,
      ),
    );
    options.onStatus("ready");
  };

  const drainRender = async (): Promise<void> => {
    if (renderInFlight || disposed) {
      return;
    }
    renderInFlight = true;
    try {
      while (renderQueued && !disposed) {
        renderQueued = false;
        const generation = renderGeneration;
        const diff = current;
        try {
          await render(generation, diff);
        } catch (error) {
          if (!disposed && generation === renderGeneration && diff === current) {
            log("error", `unified review rendering failed: ${String(error)}`);
            applyMarkers(null);
            options.onStatus("failed");
          }
        }
      }
    } finally {
      renderInFlight = false;
      if (renderQueued && !disposed) {
        void drainRender();
      }
    }
  };

  const queueRender = (): void => {
    renderQueued = true;
    void drainRender();
  };

  const scheduleRender = (): void => {
    renderGeneration++;
    if (timer !== undefined) {
      clearTimeout(timer);
    }
    timer = setTimeout(() => {
      timer = undefined;
      queueRender();
    }, DIFF_RECOMPUTE_DEBOUNCE_MS);
  };

  const subscriptions = [
    model.onDidChangeContent(scheduleRender),
    editor.onDidContentSizeChange(measure),
  ];
  queueRender();

  return {
    painted: () => painted,
    changeLines: () =>
      markers === null ? [] : markers.hunks.map((hunk) => hunk.anchorLine).sort((a, b) => a - b),
    topForLine: (line) => editor.getTopForLineNumber(line),
    update: (next: ReviewFileDiff) => {
      current = next;
      renderGeneration++;
      if (timer !== undefined) {
        clearTimeout(timer);
        timer = undefined;
      }
      queueRender();
    },
    dispose: () => {
      disposed = true;
      renderGeneration++;
      renderQueued = false;
      computer.dispose();
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
