import { type Accessor, createSignal, onCleanup } from "solid-js";

/** Which sides of a horizontally scrolling element have content scrolled out of view. */
export interface HorizontalOverflow {
  left: boolean;
  right: boolean;
}

/**
 * Follows the overflow of the element handed to `track` (use it as a ref) as it scrolls, resizes, or its content
 * changes, until the owner that tracked it is disposed.
 */
export function createHorizontalOverflow(): {
  overflow: Accessor<HorizontalOverflow>;
  track: (element: HTMLElement) => void;
} {
  const [overflow, setOverflow] = createSignal<HorizontalOverflow>(
    { left: false, right: false },
    { equals: (a, b) => a.left === b.left && a.right === b.right },
  );
  const track = (element: HTMLElement): void => {
    const measure = (): void => {
      const max = element.scrollWidth - element.clientWidth;
      setOverflow({ left: element.scrollLeft > 1, right: element.scrollLeft < max - 1 });
    };
    const resize = new ResizeObserver(measure);
    const content = new MutationObserver(measure);
    element.addEventListener("scroll", measure, { passive: true });
    resize.observe(element);
    content.observe(element, { childList: true, subtree: true, characterData: true });
    onCleanup(() => {
      element.removeEventListener("scroll", measure);
      resize.disconnect();
      content.disconnect();
    });
  };
  return { overflow, track };
}
