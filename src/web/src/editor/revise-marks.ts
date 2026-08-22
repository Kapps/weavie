// The in-flight half of Revise: while the host is revising a region, that region is tinted and carries a pill
// counting how long it has been running. There is no progress to report — the query is one shot with no
// streaming — so elapsed time is the only honest signal. The decoration also anchors the write: Monaco moves it
// with the text, so `verify` compares what the region holds NOW against what the host captured.
import * as monaco from "monaco-editor";
import { type ClientSession, selectedSession } from "../bridge";
import { dirtyPathsFor } from "./dirty-store";
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
  activePath: () => string | null;
}

export interface ReviseMarks {
  /** Replaces `session`'s in-flight set. Regions render only while that session is the selected one. */
  set(session: ClientSession, regions: ReviseRegion[]): void;
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
  deps: ReviseMarksDeps,
): ReviseMarks {
  // Regions arrive on every loaded session's bus, so they are kept per session and only the selected session's
  // are rendered; one shared set would let another session's retire wipe this one's tint.
  const bySession = new Map<ClientSession, ReviseRegion[]>();
  let rendered: Rendered[] = [];
  let ticker: ReturnType<typeof setInterval> | undefined;
  const startedAt = new Map<ClientSession, Map<number, number>>();

  const elapsed = (session: ClientSession, id: number): string => {
    const started = startedAt.get(session)?.get(id) ?? Date.now();
    return `Revising… ${Math.max(0, Math.round((Date.now() - started) / 1000))}s`;
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
    const session = selectedSession();
    const model = editor.getModel();
    const active = deps.activePath();
    if (session === null || model === null || active === null) {
      return;
    }

    for (const region of bySession.get(session) ?? []) {
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
      pill.textContent = elapsed(session, region.id);
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
          entry.pill.textContent = elapsed(session, entry.region.id);
        }
      }, 1000);
    }
  };

  // A model swap must re-render, or the pill stays anchored over whatever file is now showing and `verify`
  // reads a decoration belonging to the previous model.
  const modelListener = editor.onDidChangeModel(() => render());

  return {
    set(session: ClientSession, regions: ReviseRegion[]): void {
      if (regions.length === 0) {
        bySession.delete(session);
      } else {
        bySession.set(session, regions);
      }
      const live = new Set(regions.map((region) => region.id));
      const started = startedAt.get(session) ?? new Map<number, number>();
      for (const id of [...started.keys()]) {
        if (!live.has(id)) {
          started.delete(id);
        }
      }
      for (const region of regions) {
        if (!started.has(region.id)) {
          started.set(region.id, Date.now());
        }
      }
      if (started.size === 0) {
        startedAt.delete(session);
      } else {
        startedAt.set(session, started);
      }
      render();
    },
    verify(session: ClientSession, id: number): string | null {
      const region = (bySession.get(session) ?? []).find((candidate) => candidate.id === id);
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
      if (session !== selectedSession() || entry === undefined || model === null) {
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
      bySession.clear();
      startedAt.clear();
      modelListener.dispose();
      teardown();
    },
  };
}
