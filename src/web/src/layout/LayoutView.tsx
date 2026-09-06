import { createEffect, createMemo, createSignal, For, type JSX, onCleanup } from "solid-js";
import { floatingPanelLevel } from "../chrome/floating-panels";
import { notify } from "../notify/notify";
import {
  computeRects,
  computeSplitters,
  type Rect,
  type SplitterInfo,
  setBoundary,
} from "./geometry";
import { type LayoutNode, PANE_KINDS } from "./types";

// Pane kinds are singletons in v1, so slots are a stable, never-reordered list — surfaces are repositioned,
// not remounted (a remount would wipe terminal scrollback / editor state). The tree drives geometry only.

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
  floating: (kind: string) => boolean;
  onResize: (expected: LayoutNode, root: LayoutNode) => Promise<void>;
}): JSX.Element {
  let container!: HTMLDivElement;
  const [preview, setPreview] = createSignal<LayoutNode | null>(null);
  const rects = createMemo(() => computeRects(preview() ?? props.root));
  const splitters = createMemo(() => computeSplitters(preview() ?? props.root));

  // Tears down the in-flight drag's window listeners; set while a drag is active so a new drag, a stray
  // pointerup, OR an unmount mid-drag can all remove them (the window listeners would otherwise outlive the
  // component and keep firing onResize against a detached tree).
  let endDrag: (() => void) | null = null;

  const startDrag = (splitter: SplitterInfo, event: PointerEvent): void => {
    event.preventDefault();
    endDrag?.(); // never stack two drags
    setPreview(null);
    const expected = props.root;
    const onMove = (move: PointerEvent): void => {
      const box = container.getBoundingClientRect();
      const pct =
        splitter.dir === "row"
          ? ((move.clientX - box.left) / box.width) * 100
          : ((move.clientY - box.top) / box.height) * 100;
      const fraction = (pct - splitter.axisStart) / (splitter.axisSize || 1);
      setPreview(setBoundary(expected, splitter.path, splitter.index, fraction));
    };
    const onUp = (): void => {
      const root = preview();
      endDrag?.();
      if (root !== null) {
        setPreview(root);
        void props.onResize(expected, root).catch((error: unknown) => {
          if (preview() === root) setPreview(null);
          notify("warn", String(error));
        });
      }
    };
    endDrag = (): void => {
      window.removeEventListener("pointermove", onMove);
      window.removeEventListener("pointerup", onUp);
      window.removeEventListener("pointercancel", onCancel);
      window.removeEventListener("keydown", onKey, true);
      setPreview(null);
      endDrag = null;
    };
    const onCancel = (): void => endDrag?.();
    const onKey = (event: KeyboardEvent): void => {
      if (event.key !== "Escape") return;
      event.preventDefault();
      event.stopImmediatePropagation();
      endDrag?.();
    };
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    window.addEventListener("pointercancel", onCancel);
    window.addEventListener("keydown", onKey, true);
  };

  onCleanup(() => endDrag?.());
  createEffect(() => {
    props.root;
    endDrag?.();
    setPreview(null);
  });

  return (
    <div class="layout-root" ref={container}>
      <For each={PANE_KINDS}>
        {(kind) => (
          <div
            class="pane-slot"
            classList={{ "floating-slot": props.floating(kind) }}
            style={
              props.floating(kind)
                ? `z-index:${floatingPanelLevel(kind)}`
                : slotStyle(rects().get(kind))
            }
          >
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
