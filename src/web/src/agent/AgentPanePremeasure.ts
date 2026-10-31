import type { Virtualizer } from "@tanstack/solid-virtual";
import { type Accessor, createSignal, onCleanup, onMount } from "solid-js";

// Each batch's corrections mark the virtualizer scrolling until they settle, so batches are coarse.
const rowsPerBatch = 24;
// Outlasts the virtualizer's own post-touchend window, during which it still defers corrections.
const touchSettleMs = 200;

// The virtualizer can't correct scroll position under an iOS touch (writing scrollTop cancels momentum),
// so a row first measured mid-gesture shoves the content being read. Touch devices therefore measure the
// history above the viewport while idle, before a gesture can reach it.
export function createAgentPanePremeasure(
  body: Accessor<HTMLDivElement | undefined>,
  virtualizer: () => Virtualizer<HTMLDivElement, HTMLDivElement>,
) {
  const enabled = window.matchMedia("(any-pointer: coarse)").matches;
  const [rows, setRows] = createSignal<number[]>([], {
    equals: (previous, next) =>
      previous.length === next.length && previous.every((row, index) => row === next[index]),
  });
  let frame: number | null = null;
  let touching = false;
  let settleTimer: number | null = null;

  const step = (): void => {
    frame = null;
    const instance = virtualizer();
    if (touching || settleTimer !== null || instance.isScrolling || instance.range === null) return;
    const next: number[] = [];
    for (let index = instance.range.startIndex - 1; index >= 0; index--) {
      if (!instance.itemSizeCache.has(instance.options.getItemKey(index))) next.push(index);
      if (next.length === rowsPerBatch) break;
    }
    setRows(next.reverse());
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
    element.addEventListener("touchcancel", end, { passive: true });
    schedule();
    onCleanup(() => {
      if (frame !== null) cancelAnimationFrame(frame);
      if (settleTimer !== null) clearTimeout(settleTimer);
      element.removeEventListener("touchstart", start);
      element.removeEventListener("touchend", end);
      element.removeEventListener("touchcancel", end);
    });
  });

  return { rows, schedule };
}
