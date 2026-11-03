import type { VirtualItem, Virtualizer } from "@tanstack/solid-virtual";

/**
 * The row a reader who isn't following keeps still: its top's height above the viewport bottom, in the
 * virtualizer's bottom-up measurements, so keeping it never reads layout.
 */
export function createReadingAnchor(
  virtualizer: () => Virtualizer<HTMLDivElement, HTMLDivElement>,
  rowOf: (id: string) => VirtualItem | undefined,
) {
  let anchor: { id: string; top: number; unsized: Map<string, number> } | null = null;
  const measured = (key: string): boolean => virtualizer().itemSizeCache.has(key);

  // Keeping the screen still, a first measurement of a row the screen already shows replaces an estimate it never
  // showed, so it moves nothing. Placing a row at the top, every measurement moves the position toward it.
  const anchorOn = (row: VirtualItem, position: number, keepScreen: boolean): void => {
    const id = String(row.key);
    const unsized = new Map<string, number>();
    if (keepScreen && !measured(id)) unsized.set(id, row.size);
    for (const item of keepScreen ? virtualizer().getVirtualItems() : []) {
      const key = String(item.key);
      const shown = virtualizer().elementsCache.get(key)?.isConnected === true;
      if (item.index < row.index && shown && !measured(key)) unsized.set(key, item.size);
    }
    anchor = { id, top: row.end - position, unsized };
  };

  return {
    /** Anchors on the topmost row in view. */
    pick(position: number, viewport: number): void {
      const row = virtualizer().getVirtualItemForOffset(position + viewport - 1);
      if (row === undefined) anchor = null;
      else anchorOn(row, position, true);
    },
    /** Anchors on a row just placed at the viewport top, which measurements keep there. */
    reveal: (row: VirtualItem, position: number): void => anchorOn(row, position, false),
    /** Whether every rendered row from the newest through virtual `index` has its measured size. */
    measuredThrough: (index: number): boolean =>
      virtualizer()
        .getVirtualItems()
        .every((item) => item.index > index || measured(String(item.key))),
    /** The scroll position that keeps the anchor still, or null with nothing to keep. */
    target(): number | null {
      const row = anchor === null ? undefined : rowOf(anchor.id);
      if (anchor === null || row === undefined) return null;
      for (const [id, size] of anchor.unsized) {
        if (!measured(id)) continue;
        anchor.unsized.delete(id);
        anchor.top += (rowOf(id)?.size ?? size) - size;
      }
      return row.end - anchor.top;
    },
  };
}
