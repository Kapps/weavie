// The in-flight half of Revise: while the host is revising a region, that region is tinted and carries a pill
// counting how long it has been running. There is no progress to report — the query is one shot with no
// streaming — so elapsed time is the only honest signal. The decoration also anchors the write: Monaco moves it
// with the text, so `verify` compares what the region holds NOW against what the host captured.
import * as monaco from "monaco-editor";
import { isDirtyPath } from "./dirty-store";
import { normalizePath } from "./fs-path";

/** One region the host is currently revising. */
export interface ReviseRegion {
  id: number;
  path: string;
  startLine: number;
  endLineExclusive: number;
  originalText: string;
}

export interface ReviseMarksDeps {
  /** The host path of the editor's current model, or null when it isn't a session file. */
  activePath(): string | null;
}

export interface ReviseMarks {
  /** Replaces the in-flight set and re-renders. */
  set(regions: ReviseRegion[]): void;
  /** Re-renders the marks, e.g. after a tab switch brings a different file's regions into view. */
  refresh(): void;
  /** Null when region `id`'s write may land, else the reason it must not. */
  verify(id: number): string | null;
  dispose(): void;
}

interface Rendered {
  region: ReviseRegion;
  decorations: monaco.editor.IEditorDecorationsCollection;
  pill: HTMLElement;
  widget: monaco.editor.IContentWidget;
}

export function createReviseMarks(
  editor: monaco.editor.IStandaloneCodeEditor,
  deps: ReviseMarksDeps,
): ReviseMarks {
  let regions: ReviseRegion[] = [];
  let rendered: Rendered[] = [];
  let ticker: ReturnType<typeof setInterval> | undefined;
  const startedAt = new Map<number, number>();

  const elapsed = (id: number): string => {
    const started = startedAt.get(id) ?? Date.now();
    return `Revising… ${Math.max(0, Math.round((Date.now() - started) / 1000))}s`;
  };

  const tick = (): void => {
    for (const entry of rendered) {
      entry.pill.textContent = elapsed(entry.region.id);
    }
  };

  const teardown = (): void => {
    for (const entry of rendered) {
      entry.decorations.clear();
      editor.removeContentWidget(entry.widget);
    }
    rendered = [];
    if (ticker !== undefined) {
      clearInterval(ticker);
      ticker = undefined;
    }
  };

  const render = (): void => {
    teardown();
    const model = editor.getModel();
    const active = deps.activePath();
    if (model === null || active === null) {
      return;
    }

    for (const region of regions) {
      if (normalizePath(region.path) !== normalizePath(active)) {
        continue;
      }

      const decorations = editor.createDecorationsCollection([
        {
          range: new monaco.Range(region.startLine, 1, region.endLineExclusive - 1, 1),
          options: {
            isWholeLine: true,
            className: "weavie-revising",
            // The region must not swallow text typed at its edges, or the guard would compare the wrong lines.
            stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
          },
        },
      ]);
      const pill = document.createElement("span");
      pill.className = "weavie-revising-pill";
      pill.textContent = elapsed(region.id);
      const widget: monaco.editor.IContentWidget = {
        getId: () => `weavie.revising.${region.id}`,
        getDomNode: () => pill,
        getPosition: () => ({
          position: {
            lineNumber: region.startLine,
            column: model.getLineMaxColumn(region.startLine),
          },
          preference: [monaco.editor.ContentWidgetPositionPreference.EXACT],
        }),
      };
      editor.addContentWidget(widget);
      rendered.push({ region, decorations, pill, widget });
    }

    if (rendered.length > 0) {
      ticker = setInterval(tick, 1000);
    }
  };

  return {
    set(next: ReviseRegion[]): void {
      regions = next;
      const live = new Set(next.map((region) => region.id));
      for (const id of [...startedAt.keys()]) {
        if (!live.has(id)) {
          startedAt.delete(id);
        }
      }
      for (const region of next) {
        if (!startedAt.has(region.id)) {
          startedAt.set(region.id, Date.now());
        }
      }
      render();
    },
    refresh: render,
    verify(id: number): string | null {
      const region = regions.find((candidate) => candidate.id === id);
      if (region === undefined) {
        return "the revision is no longer tracked";
      }

      // VS Code skips resolving a dirty model, so a host write would be dropped and then lost to the next
      // autosave. Refusing is the only honest answer.
      if (isDirtyPath(region.path)) {
        return "the file has unsaved changes";
      }

      const entry = rendered.find((candidate) => candidate.region.id === id);
      const model = editor.getModel();
      if (entry === undefined || model === null) {
        return null; // Not on screen: nothing here can contradict the host's own content guard.
      }

      const range = entry.decorations.getRange(0);
      if (range === null) {
        return "the region was deleted";
      }

      const current = model.getValueInRange({
        startLineNumber: range.startLineNumber,
        startColumn: 1,
        endLineNumber: range.endLineNumber,
        endColumn: model.getLineMaxColumn(range.endLineNumber),
      });
      return current === region.originalText
        ? null
        : "the region changed while it was being revised";
    },
    dispose(): void {
      regions = [];
      startedAt.clear();
      teardown();
    },
  };
}
