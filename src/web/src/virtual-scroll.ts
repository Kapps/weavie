import { elementScroll, type Virtualizer } from "@tanstack/solid-virtual";

/** Applies measurement corrections to the live offset, including wheel motion not yet notified. */
export function scrollVirtualElement<T extends Element, U extends Element>(
  offset: number,
  options: Parameters<typeof elementScroll>[1],
  instance: Virtualizer<T, U>,
  commitGeometry: () => void,
): number {
  const top =
    options.adjustments === undefined
      ? offset
      : (instance.scrollElement?.scrollTop ?? offset) + options.adjustments;
  commitGeometry();
  elementScroll(top, { ...options, adjustments: 0 }, instance);
  return top;
}
