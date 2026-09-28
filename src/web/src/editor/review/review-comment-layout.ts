import {
  dispose,
  toDisposable,
} from "@codingame/monaco-vscode-api/vscode/vs/base/common/lifecycle";
import { createRenderEffect, createRoot, createSignal, untrack } from "solid-js";
import type { monaco } from "../monaco-setup";
import { createReviewCommentPresenter } from "./review-comment-view";

type CommentZone = monaco.editor.IViewZone & { heightInPx: number };
export interface ReviewCommentGeometry {
  zone: CommentZone;
  line: number;
  height: number;
  ordinal: number;
}

/** A file owns comment DOM; passive and live views only reserve and position its space. */
export function createReviewCommentLayout(
  host: HTMLElement,
  model: () => monaco.editor.ITextModel | null,
) {
  const held = new Set<CommentZone>();
  let owner: object | undefined;
  let passive: { positions: ReadonlyMap<CommentZone, number>; contentLeft: number } | undefined;
  const placePassive = (): void => {
    if (owner || !passive) return;
    host.style.left = `${passive.contentLeft}px`;
    for (const [zone, top] of passive.positions) {
      if (held.has(zone)) zone.domNode.style.top = `${top}px`;
    }
  };
  const [geometry, setGeometry] = createSignal<readonly ReviewCommentGeometry[]>([]);
  const changed = (): void => {
    setGeometry(
      [...held].map((zone) => ({
        zone,
        line: zone.afterLineNumber,
        height: zone.heightInPx,
        ordinal: zone.ordinal!,
      })),
    );
  };
  const presenter = createReviewCommentPresenter({
    model,
    place: (zone) => {
      zone.domNode.style.cssText = "position:absolute;left:0;right:0";
      host.append(zone.domNode);
      held.add(zone);
      changed();
      return {
        layout: changed,
        dispose: () => {
          held.delete(zone);
          zone.domNode.remove();
          changed();
        },
      };
    },
  });
  return {
    presenter,
    geometry,
    place: (positions: ReadonlyMap<CommentZone, number>, contentLeft: number) => {
      passive = { positions, contentLeft };
      placePassive();
    },
    bind: (editor: monaco.editor.IStandaloneCodeEditor, update: (change: () => void) => void) => {
      if (owner) throw new Error("Review comments already have a live presentation");
      const token = {};
      owner = token;
      const zones = new Map<CommentZone, { id: string; value: CommentZone }>();
      let disposeEffect = () => {};
      let layoutSubscription: { dispose(): void } | undefined;
      const align = (): void => {
        if (owner === token) host.style.left = `${editor.getLayoutInfo().contentLeft}px`;
      };
      const release = (): void => {
        if (owner !== token) return;
        owner = undefined;
        try {
          dispose([
            toDisposable(disposeEffect),
            toDisposable(() => layoutSubscription?.dispose()),
            toDisposable(() =>
              update(() =>
                editor.changeViewZones((accessor) => {
                  for (const item of zones.values()) accessor.removeZone(item.id);
                }),
              ),
            ),
          ]);
        } finally {
          zones.clear();
          placePassive();
        }
      };
      try {
        align();
        layoutSubscription = editor.onDidLayoutChange(align);
        createRoot((dispose) => {
          disposeEffect = dispose;
          createRenderEffect(() => {
            const wanted = geometry();
            untrack(() =>
              update(() =>
                editor.changeViewZones((accessor) => {
                  const remaining = new Set(wanted.map((item) => item.zone));
                  for (const [zone, item] of zones) {
                    if (remaining.has(zone)) continue;
                    accessor.removeZone(item.id);
                    zones.delete(zone);
                  }
                  for (const item of wanted) {
                    const existing = zones.get(item.zone);
                    if (existing) {
                      existing.value.afterLineNumber = item.line;
                      existing.value.heightInPx = item.height;
                      accessor.layoutZone(existing.id);
                    } else {
                      const value: CommentZone = {
                        afterLineNumber: item.line,
                        heightInPx: item.height,
                        ordinal: item.ordinal,
                        showInHiddenAreas: true,
                        domNode: document.createElement("div"),
                        onDomNodeTop: (top) => {
                          if (owner === token)
                            item.zone.domNode.style.top = `${top + editor.getScrollTop()}px`;
                        },
                      };
                      zones.set(item.zone, { value, id: accessor.addZone(value) });
                    }
                  }
                }),
              ),
            );
          });
        });
      } catch (error) {
        try {
          release();
        } catch (cleanup) {
          throw new AggregateError([error, cleanup], "Review comment binding failed");
        }
        throw error;
      }
      return { dispose: release };
    },
    dispose: () => presenter.dispose(),
  };
}

export type ReviewCommentLayout = ReturnType<typeof createReviewCommentLayout>;
