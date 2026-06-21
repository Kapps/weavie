import { For, type JSX, createMemo } from "solid-js";
import {
  type Rect,
  type SplitterInfo,
  computeRects,
  computeSplitters,
  setBoundary,
} from "./geometry";
import type { LayoutNode } from "./types";

// Pane kinds are singletons in v1, so the slots are a stable, never-reordered list. This keeps the live
// xterm / Monaco surfaces repositioned rather than remounted (a remount would wipe terminal scrollback /
// editor state). The tree drives geometry only.
const KINDS = ["terminal:claude", "terminal:shell", "editor"] as const;

function slotStyle(rect: Rect | undefined): string {
  if (rect === undefined) {
    return "display:none";
  }
  return `left:${rect.x}%;top:${rect.y}%;width:${rect.w}%;height:${rect.h}%`;
}

function handleStyle(splitter: SplitterInfo): string {
  return splitter.dir === "row"
    ? `left:${splitter.x}%;top:${splitter.y}%;height:${splitter.cross}%`
    : `left:${splitter.x}%;top:${splitter.y}%;width:${splitter.cross}%`;
}

export function LayoutView(props: {
  root: LayoutNode;
  renderPane: (kind: string) => JSX.Element;
  onResize: (root: LayoutNode) => void;
}): JSX.Element {
  let container!: HTMLDivElement;
  const rects = createMemo(() => computeRects(props.root));
  const splitters = createMemo(() => computeSplitters(props.root));

  const startDrag = (splitter: SplitterInfo, event: PointerEvent): void => {
    event.preventDefault();
    const onMove = (move: PointerEvent): void => {
      const box = container.getBoundingClientRect();
      const pct =
        splitter.dir === "row"
          ? ((move.clientX - box.left) / box.width) * 100
          : ((move.clientY - box.top) / box.height) * 100;
      const fraction = (pct - splitter.axisStart) / (splitter.axisSize || 1);
      props.onResize(setBoundary(props.root, splitter.path, splitter.index, fraction));
    };
    const onUp = (): void => {
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
    };
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
  };

  return (
    <div class="layout-root" ref={container}>
      <For each={KINDS}>
        {(kind) => (
          <div class="pane-slot" style={slotStyle(rects().get(kind))}>
            {props.renderPane(kind)}
          </div>
        )}
      </For>
      <For each={splitters()}>
        {(splitter) => (
          <div
            class={splitter.dir === "row" ? "split-handle vertical" : "split-handle horizontal"}
            style={handleStyle(splitter)}
            onPointerDown={(event) => startDrag(splitter, event)}
          />
        )}
      </For>
    </div>
  );
}
