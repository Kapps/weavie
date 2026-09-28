import {
  type createVirtualizer,
  elementScroll,
  observeElementOffset,
  observeElementRect,
  type VirtualItem,
  Virtualizer,
} from "@tanstack/solid-virtual";
import { batch, createComputed, createSignal, onCleanup, onMount, untrack } from "solid-js";

type Options = Parameters<typeof createVirtualizer<HTMLElement, HTMLElement>>[0];
type Row = Readonly<VirtualItem>;

const sameRow = (first: Row, second: Row): boolean =>
  first.key === second.key &&
  first.index === second.index &&
  first.start === second.start &&
  first.end === second.end &&
  first.size === second.size &&
  first.lane === second.lane;

/** Core owns geometry and scroll compensation; Solid receives immutable changed geometry only. */
export function createReviewLayout(options: () => Options) {
  const defaults = { observeElementRect, observeElementOffset, scrollToFn: elementScroll };
  const instance = new Virtualizer<HTMLElement, HTMLElement>({ ...defaults, ...untrack(options) });
  const [rows, setRows] = createSignal<readonly Row[]>([]);
  const [totalSize, setTotalSize] = createSignal(0);
  const publish = (): void => {
    const current = instance.getVirtualItems();
    const previous = untrack(rows);
    const unchanged =
      current.length === previous.length &&
      current.every((row, index) => sameRow(previous[index]!, row));
    const next = unchanged
      ? previous
      : current.map((row, index) =>
          previous[index] && sameRow(previous[index]!, row) ? previous[index]! : { ...row },
        );
    const size = instance.getTotalSize();
    batch(() => {
      setRows(next);
      setTotalSize(size);
    });
  };
  onCleanup(instance._didMount());
  onMount(() => untrack(instance._willUpdate));
  createComputed(() => {
    const next = options();
    const element = next.getScrollElement();
    untrack(() => {
      instance.setOptions({
        ...defaults,
        ...next,
        getScrollElement: () => element,
        onChange: (current, sync) =>
          untrack(() => {
            current._willUpdate();
            publish();
            next.onChange?.(current, sync);
          }),
      });
      instance._willUpdate();
      publish();
    });
  });
  return { instance, rows, totalSize };
}
