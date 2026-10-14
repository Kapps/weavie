/**
 * Whether a resized file moves the scroll position with it: TanStack's default minus its skip while scrolling up.
 * That skip guards native momentum scrolling; this scroll is owned and carries glides through corrections, so the
 * skip only made a file above jump the view when its real height arrived mid-scroll.
 */
export function keepFold<Key>(
  item: { key: Key; start: number; end: number },
  _delta: number,
  list: { scrollOffset: number | null; scrollAdjustments: number; itemSizeCache: Map<Key, number> },
): boolean {
  const fold = (list.scrollOffset ?? 0) + list.scrollAdjustments;
  // A re-measured file spanning the fold grows below what's on screen, so only one wholly above moves the view.
  return list.itemSizeCache.has(item.key) ? item.end <= fold : item.start < fold;
}
