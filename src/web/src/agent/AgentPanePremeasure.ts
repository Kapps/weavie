import type { Virtualizer } from "@tanstack/solid-virtual";
import { type Accessor, createSignal, onCleanup, onMount } from "solid-js";

// Each batch's corrections mark the virtualizer scrolling until they settle, so batches are coarse.
const rowsPerBatch = 24;
// Outlasts the virtualizer's own post-touchend window, during which it still defers corrections.
const touchSettleMs = 200;

/** A row mounted for measurement; its key detects an index the layout has since moved. */
export interface PremeasuredRow {
  index: number;
  key: string;
}

// The virtualizer can't correct scroll position under an iOS touch (writing scrollTop cancels momentum),
// so a row first measured mid-gesture shoves the content being read. Touch devices therefore measure the
// history above the viewport while idle, before a gesture can reach it.
export function createAgentPanePremeasure(
  body: Accessor<HTMLDivElement | undefined>,
  virtualizer: () => Virtualizer<HTMLDivElement, HTMLDivElement>,
) {
  const enabled = window.matchMedia("(any-pointer: coarse)").matches;
  const [rows, setRows] = createSignal<PremeasuredRow[]>([]);
  // A row measured at exactly its estimate never enters the size cache, so mounting is what counts.
  const mounted = new Set<string>();
  let frame: number | null = null;
  let touching = false;
  let settleTimer: number | null = null;

  const step = (): void => {
    frame = null;
    const instance = virtualizer();
    if (touching || settleTimer !== null || instance.isScrolling || instance.range === null) return;
    const next: PremeasuredRow[] = [];
    for (let index = instance.range.startIndex - 1; index >= 0; index--) {
      const key = String(instance.options.getItemKey(index));
      if (instance.itemSizeCache.has(key) || mounted.has(key)) continue;
      mounted.add(key);
      next.push({ index, key });
      if (next.length === rowsPerBatch) break;
    }
    if (next.length > 0 || rows().length > 0) setRows(next);
    if (next.length > 0) schedule();
  };

  const schedule = (): void => {
    if (enabled && frame === null) frame = requestAnimationFrame(step);
  };

  onMount(() => {
    const element = body();
    if (!enabled || element === undefined) return;
    const start = (): void => {
      touching = true;
    };
    const end = (): void => {
      touching = false;
      if (settleTimer !== null) clearTimeout(settleTimer);
      settleTimer = window.setTimeout(() => {
        settleTimer = null;
        schedule();
      }, touchSettleMs);
    };
    element.addEventListener("touchstart", start, { passive: true });
    element.addEventListener("touchend", end, { passive: true });
    schedule();
    onCleanup(() => {
      if (frame !== null) cancelAnimationFrame(frame);
      if (settleTimer !== null) clearTimeout(settleTimer);
      element.removeEventListener("touchstart", start);
      element.removeEventListener("touchend", end);
    });
  });

  return { rows, schedule };
}
