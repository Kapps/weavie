// The in-flight half of Revise: while the host is revising a region, that region is tinted and carries a pill
// counting how long it has been running. There is no progress to report — the query is one shot with no
// streaming — so elapsed time is the only honest signal. The decoration also anchors the write: Monaco moves it
// with the text, so `verify` compares what the region holds NOW against what the host captured.
import * as monaco from "monaco-editor";
import type { ClientSession } from "../bridge";
import { dirtyPathsFor } from "./dirty-store";
import { normalizePath } from "./fs-path";
import { SESSION_FILE_SCHEME, sessionForUri, sessionUriHostPath } from "./session-uri-owner";

/** One region the host is currently revising. */
export interface ReviseRegion {
  id: number;
  path: string;
  startLine: number;
  endLineExclusive: number;
  originalText: string;
}

/** One session-owned revision state shared by every view of its working copies. */
export function createReviseState() {
  const bySession = new Map<ClientSession, ReviseRegion[]>();
  const startedAt = new Map<ReviseRegion, number>();
  return {
    regions: (session: ClientSession): ReviseRegion[] => bySession.get(session) ?? [],
    elapsed: (region: ReviseRegion): string =>
      `Revising… ${Math.max(0, Math.round((Date.now() - startedAt.get(region)!) / 1000))}s`,
    set: (session: ClientSession, regions: ReviseRegion[]): void => {
      const previous = bySession.get(session) ?? [];
      for (const region of regions) {
        const existing = previous.find((candidate) => candidate.id === region.id);
        startedAt.set(region, existing === undefined ? Date.now() : startedAt.get(existing)!);
      }
      for (const region of previous) {
        if (!regions.includes(region)) startedAt.delete(region);
      }
      if (regions.length === 0) bySession.delete(session);
      else bySession.set(session, regions);
    },
    dispose: (): void => {
      bySession.clear();
      startedAt.clear();
    },
  };
}

type ReviseState = ReturnType<typeof createReviseState>;

export interface ReviseMarks {
  refresh(): void;
  /** Null when region `id` of `session` may be written, else the reason it must not. */
  verify(session: ClientSession, id: number): string | null;
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
  state: ReviseState,
): ReviseMarks {
  let rendered: Rendered[] = [];
  let ticker: ReturnType<typeof setInterval> | undefined;

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
    if (model === null || model.uri.scheme !== SESSION_FILE_SCHEME) return;
    const session = sessionForUri(model.uri);
    const active = sessionUriHostPath(model.uri);
    if (session === undefined) {
      return;
    }

    for (const region of state.regions(session)) {
      if (normalizePath(region.path) !== normalizePath(active)) {
        continue;
      }

      const decorations = editor.createDecorationsCollection([
        {
          range: new monaco.Range(region.startLine, 1, region.endLineExclusive - 1, 1),
          options: {
            isWholeLine: true,
            className: "weavie-revising",
            // The region must not swallow text typed at its edges, or the guard would cover the wrong lines.
            stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
          },
        },
      ]);
      const pill = document.createElement("span");
      pill.className = "weavie-revising-pill";
      pill.textContent = state.elapsed(region);
      const widget: monaco.editor.IContentWidget = {
        getId: () => `weavie.revising.${region.id}`,
        getDomNode: () => pill,
        // Anchored at the region's left edge, above the first line: at the line's END a long first line pushes
        // the pill past the viewport and Monaco clips it, so the user gets the wash and no elapsed indicator.
        getPosition: () => ({
          position: { lineNumber: region.startLine, column: 1 },
          preference: [
            monaco.editor.ContentWidgetPositionPreference.ABOVE,
            monaco.editor.ContentWidgetPositionPreference.BELOW,
          ],
        }),
      };
      editor.addContentWidget(widget);
      rendered.push({ region, decorations, pill, widget });
    }

    if (rendered.length > 0) {
      ticker = setInterval(() => {
        for (const entry of rendered) {
          entry.pill.textContent = state.elapsed(entry.region);
        }
      }, 1000);
    }
  };

  // A model swap must re-render, or the pill stays anchored over whatever file is now showing and `verify`
  // reads a decoration belonging to the previous model.
  const modelListener = editor.onDidChangeModel(render);
  render();

  return {
    refresh: render,
    verify(session: ClientSession, id: number): string | null {
      const region = state.regions(session).find((candidate) => candidate.id === id);
      if (region === undefined) {
        return "the revision is no longer tracked";
      }

      // VS Code skips resolving a dirty model, so a host write would be dropped and then lost to the next
      // autosave. Refusing is the only honest answer.
      if (dirtyPathsFor(session).has(normalizePath(region.path))) {
        return "the file has unsaved changes";
      }

      const entry = rendered.find((candidate) => candidate.region.id === id);
      const model = editor.getModel();
      if (entry === undefined || model === null || sessionForUri(model.uri) !== session) {
        return null; // Not on screen: nothing here can contradict the host's own content guard.
      }

      const range = entry.decorations.getRange(0);
      if (range === null) {
        return "the region was deleted";
      }

      // Line content joined with \n, matching the guard the host compares against.
      const current = model
        .getLinesContent()
        .slice(range.startLineNumber - 1, range.endLineNumber)
        .join("\n");
      return current === region.originalText
        ? null
        : "the region changed while it was being revised";
    },
    dispose(): void {
      modelListener.dispose();
      teardown();
    },
  };
}
