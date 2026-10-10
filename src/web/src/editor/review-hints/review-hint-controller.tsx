// Shows the session's review hints in one editor binding: a margin badge, a line wash and an end-of-line pill per
// hint, anchored to the live buffer, plus the hint popover as a content widget that overlays code without moving it.

import { createEffect, createMemo, createRoot, createSignal, For, onCleanup, Show } from "solid-js";
import { render } from "solid-js/web";
import type { ClientSession } from "../../bridge";
import { describeError } from "../../lsp/lsp-errors";
import { notify } from "../../notify/notify";
import { samePath } from "../fs-path";
import { monaco } from "../monaco-setup";
import { DIFF_RECOMPUTE_DEBOUNCE_MS } from "../review/diff-zones";
import { SESSION_FILE_SCHEME, sessionUriHostPath } from "../session-uri";
import { HintPill, HintPopover } from "./HintPopover";
import { type PlacedHint, placeHints } from "./hint-anchor";
import {
  fetchHintSource,
  openedHint,
  openHint,
  reviewHintsFor,
  reviewHintsVisible,
} from "./review-hints-store";
import "./review-hints.css";

export interface ReviewHintController {
  /** This file's hints on their live-buffer lines. */
  placed(): PlacedHint[];
  dispose(): void;
}

const inert: ReviewHintController = { placed: () => [], dispose: () => {} };

const HOVER_CLOSE_MS = 250;

export function createReviewHints(
  session: ClientSession,
  editor: monaco.editor.IStandaloneCodeEditor,
  model: monaco.editor.ITextModel,
): ReviewHintController {
  if (model.uri.scheme !== SESSION_FILE_SCHEME) return inert;
  const path = sessionUriHostPath(model.uri);
  return createRoot((dispose) => {
    const hints = createMemo(() =>
      reviewHintsVisible()
        ? (reviewHintsFor(session) ?? []).filter((hint) => samePath(hint.path, path))
        : [],
    );
    // Captured sources are immutable per hash, so each is fetched once; a failed fetch retries on the next push,
    // and only reports while a hint still needs that source.
    const [sources, setSources] = createSignal<ReadonlyMap<string, string>>(new Map());
    const requested = new Set<string>();
    createEffect(() => {
      for (const { sourceHash } of hints()) {
        if (requested.has(sourceHash)) continue;
        requested.add(sourceHash);
        fetchHintSource(session, path, sourceHash).then(
          (text) => setSources((current) => new Map(current).set(sourceHash, text)),
          (error: unknown) => {
            requested.delete(sourceHash);
            if (!hints().some((hint) => hint.sourceHash === sourceHash)) return;
            notify(
              "warn",
              `Couldn't load review hint lines: ${describeError(error)}`,
              "review-hints",
            );
          },
        );
      }
    });
    const [version, setVersion] = createSignal(0);
    let recompute: ReturnType<typeof setTimeout> | undefined;
    const onContent = model.onDidChangeContent(() => {
      clearTimeout(recompute);
      recompute = setTimeout(() => setVersion((value) => value + 1), DIFF_RECOMPUTE_DEBOUNCE_MS);
    });
    const placed = createMemo((): PlacedHint[] => {
      version();
      const loaded = sources();
      try {
        const ready = hints().filter((hint) => loaded.has(hint.sourceHash));
        return placeHints(ready, loaded, model.getValue());
      } catch (error) {
        notify("warn", describeError(error), "review-hints");
        return [];
      }
    });

    // Each pill is a content widget, not injected text: Monaco splits long injected text into separately
    // styled chunks, and only a real element can ellipsize at the viewport's edge.
    const decorations = editor.createDecorationsCollection();
    let pills: monaco.editor.IContentWidget[] = [];
    createEffect(() => {
      const entries = placed();
      decorations.set(
        entries.map((entry) => {
          const kind = `weavie-hint-${entry.hint.severity}${entry.changed ? " weavie-hint-changed" : ""}`;
          const end = model.getLineMaxColumn(entry.line);
          return {
            range: new monaco.Range(entry.line, end, entry.line, end),
            options: {
              isWholeLine: true,
              className: `weavie-hint-line ${kind}`,
              glyphMarginClassName: `weavie-hint-mark ${kind}`,
              stickiness: monaco.editor.TrackedRangeStickiness.NeverGrowsWhenTypingAtEdges,
            },
          };
        }),
      );
      // One widget per line, so several hints on a line sit side by side instead of stacking.
      const lines = new Map<number, { index: number; group: PlacedHint[] }>();
      entries.forEach((entry, index) => {
        const line = lines.get(entry.line);
        if (line === undefined) lines.set(entry.line, { index, group: [entry] });
        else line.group.push(entry);
      });
      pills = [...lines.values()].map(({ index, group }) => {
        const dom = document.createElement("div");
        const unmount = render(
          () => (
            <div class="weavie-hint-pills">
              <For each={group}>{(entry) => <HintPill placed={entry} />}</For>
            </div>
          ),
          dom,
        );
        const widget: monaco.editor.IContentWidget = {
          getId: () => `weavie.reviewHintPills.${entries[index]?.hint.id}`,
          getDomNode: () => dom,
          // The tracked decoration carries the line through edits until the debounced re-anchor replaces it.
          getPosition: () => {
            const line = decorations.getRange(index)?.startLineNumber;
            return line === undefined
              ? null
              : {
                  position: { lineNumber: line, column: model.getLineMaxColumn(line) },
                  preference: [monaco.editor.ContentWidgetPositionPreference.EXACT],
                  // Right of any text injected at the line end, such as the blame annotation.
                  positionAffinity: monaco.editor.PositionAffinity.Right,
                };
          },
          afterRender: (_position, coordinate) => {
            if (coordinate === null) return;
            const visibleRight = editor.getScrollLeft() + editor.getLayoutInfo().contentWidth;
            dom.style.height = `${editor.getOption(monaco.editor.EditorOption.lineHeight)}px`;
            dom.style.setProperty("--pill-room", `${visibleRight - coordinate.left}px`);
          },
        };
        editor.addContentWidget(widget);
        onCleanup(() => {
          editor.removeContentWidget(widget);
          unmount();
        });
        return widget;
      });
    });
    const relayoutPills = model.onDidChangeContent(() => {
      for (const widget of pills) editor.layoutContentWidget(widget);
    });

    const owned = () => {
      const open = openedHint(session);
      return open?.owner === editor ? open : null;
    };
    let closing: ReturnType<typeof setTimeout> | undefined;
    const pointer = (inside: boolean): void => {
      if (inside) {
        clearTimeout(closing);
        closing = undefined;
        return;
      }
      const open = owned();
      if (closing !== undefined || open === null || open.pinned) return;
      closing = setTimeout(() => {
        closing = undefined;
        if (owned() === open) openHint(session, null);
      }, HOVER_CLOSE_MS);
    };
    const hintAt = (event: monaco.editor.IEditorMouseEvent): PlacedHint | undefined => {
      const element = event.target.element;
      const pill = element?.closest<HTMLElement>(".weavie-hint-pill");
      if (pill != null)
        return placed().find((entry) => String(entry.hint.id) === pill.dataset.hintId);
      const line = event.target.position?.lineNumber;
      return element?.closest(".weavie-hint-mark") == null
        ? undefined
        : placed().find((entry) => entry.line === line);
    };
    const show = (entry: PlacedHint, pinned: boolean): void => {
      const open = owned();
      if (open?.id === entry.hint.id && (open.pinned || !pinned)) return;
      openHint(session, { id: entry.hint.id, owner: editor, pinned });
    };
    // The popover is a content widget, so Monaco reports pointer events on it too; those belong to the popover.
    const onPopover = (event: monaco.editor.IEditorMouseEvent): boolean =>
      event.target.element?.closest(".weavie-hint-hover") != null;
    const mouse = [
      editor.onMouseMove((event) => {
        if (onPopover(event)) return pointer(true);
        const entry = hintAt(event);
        if (entry === undefined) return pointer(false);
        pointer(true);
        show(entry, false);
      }),
      editor.onMouseLeave(() => pointer(false)),
      editor.onMouseDown((event) => {
        if (onPopover(event)) return;
        const entry = hintAt(event);
        if (entry !== undefined) show(entry, true);
        else if (owned() !== null) openHint(session, null);
      }),
    ];

    // Navigation opens a hint with no owner; the first displayed binding of its file claims and reveals it.
    createEffect(() => {
      const open = openedHint(session);
      if (open === null || open.owner !== null) return;
      const entry = placed().find((candidate) => candidate.hint.id === open.id);
      if (entry === undefined || editor.getContainerDomNode().getClientRects().length === 0) return;
      openHint(session, { ...open, owner: editor });
      editor.setPosition({ lineNumber: entry.line, column: 1 });
      editor.revealLineInCenterIfOutsideViewport(entry.line);
      editor.focus();
    });

    const shownId = createMemo(() => {
      const id = owned()?.id;
      return placed().some((entry) => entry.hint.id === id) ? id : undefined;
    });
    createEffect(() => {
      const id = shownId();
      if (id === undefined) return;
      const current = () => placed().find((entry) => entry.hint.id === id);
      const dom = document.createElement("div");
      dom.className = "weavie-hint-popover-anchor";
      const unmount = render(
        () => (
          <Show when={current()}>
            {(entry) => <HintPopover placed={entry()} session={session} onPointer={pointer} />}
          </Show>
        ),
        dom,
      );
      const widget: monaco.editor.IContentWidget = {
        getId: () => `weavie.reviewHint.${id}`,
        getDomNode: () => dom,
        getPosition: () => {
          const line = current()?.line;
          return line === undefined
            ? null
            : {
                position: { lineNumber: line, column: 1 },
                preference: [
                  monaco.editor.ContentWidgetPositionPreference.BELOW,
                  monaco.editor.ContentWidgetPositionPreference.ABOVE,
                ],
              };
        },
        allowEditorOverflow: true,
        suppressMouseDown: true,
      };
      editor.addContentWidget(widget);
      createEffect(() => {
        current();
        editor.layoutContentWidget(widget);
      });
      onCleanup(() => {
        editor.removeContentWidget(widget);
        unmount();
      });
    });

    return {
      placed,
      dispose: () => {
        clearTimeout(recompute);
        clearTimeout(closing);
        onContent.dispose();
        relayoutPills.dispose();
        for (const subscription of mouse) subscription.dispose();
        if (owned() !== null) openHint(session, null);
        decorations.clear();
        dispose();
      },
    };
  });
}
