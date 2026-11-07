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
  // Rows whose size came from their resize observer, even where it matched the estimate.
  const sized = new Set<string>();

  return {
    /** Records a row's size reported by its resize observer. */
    noteMeasured: (id: string): void => void sized.add(id),
    /** Forgets a row's measurement once its estimate stands in for it again. */
    forget: (id: string): void => void sized.delete(id),
    /** Anchors on the topmost row in view; a first measurement of a row on screen replaces an estimate never shown. */
    pick(position: number, viewport: number): void {
      const row = virtualizer().getVirtualItemForOffset(position + viewport - 1);
      if (row === undefined) {
        anchor = null;
        return;
      }
      const id = String(row.key);
      const unsized = new Map<string, number>();
      if (!sized.has(id)) unsized.set(id, row.size);
      for (const item of virtualizer().getVirtualItems()) {
        const key = String(item.key);
        const shown = virtualizer().elementsCache.get(key)?.isConnected === true;
        if (item.index < row.index && shown && !sized.has(key)) unsized.set(key, item.size);
      }
      anchor = { id, top: row.end - position, unsized };
    },
    /** Anchors on a row just placed at the viewport top; every measurement moves the position toward it. */
    reveal(row: VirtualItem, position: number): void {
      anchor = { id: String(row.key), top: row.end - position, unsized: new Map() };
    },
    /** Whether every rendered row from the newest through virtual `index` has been measured. */
    measuredThrough: (index: number): boolean =>
      virtualizer()
        .getVirtualItems()
        .every((item) => item.index > index || sized.has(String(item.key))),
    /** The scroll position that keeps the anchor still, or null with nothing to keep. */
    target(): number | null {
      const row = anchor === null ? undefined : rowOf(anchor.id);
      if (anchor === null || row === undefined) return null;
      for (const [id, size] of anchor.unsized) {
        if (!sized.has(id)) continue;
        anchor.unsized.delete(id);
        anchor.top += (rowOf(id)?.size ?? size) - size;
      }
      return row.end - anchor.top;
    },
  };
}
