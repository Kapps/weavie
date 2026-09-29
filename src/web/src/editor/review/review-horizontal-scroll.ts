import { ScrollableElement } from "@codingame/monaco-vscode-api/vscode/vs/base/browser/ui/scrollbar/scrollableElement";
import { ScrollbarVisibility } from "@codingame/monaco-vscode-api/vscode/vs/base/common/scrollable";
import type { ReviewHorizontalPosition } from "./review-horizontal-position";

/** The file's horizontal rail stays in its visible intersection with the outer viewport. */
export function createReviewHorizontalScroll(
  host: HTMLElement,
  position: ReviewHorizontalPosition,
) {
  const extent = document.createElement("div");
  const scrollable = new ScrollableElement(extent, {
    horizontal: ScrollbarVisibility.Auto,
    vertical: ScrollbarVisibility.Hidden,
    handleMouseWheel: false,
    useShadows: false,
  });
  const node = scrollable.getDomNode();
  node.classList.add("review-horizontal-scroll");
  node.style.cssText = "position:absolute;z-index:2";
  host.append(node);
  const scrollbar = node.querySelector<HTMLElement>(":scope > .scrollbar.horizontal")!;
  scrollbar.removeAttribute("aria-hidden");
  scrollbar.setAttribute("role", "scrollbar");
  scrollbar.setAttribute("aria-label", "File horizontal scroll position");
  scrollbar.setAttribute("aria-orientation", "horizontal");
  scrollbar.setAttribute("aria-valuemin", "0");
  scrollbar.tabIndex = 0;
  let maximum = 0;
  let viewportWidth = 0;
  let height = 0;
  let previousTop = -1;
  let applying = false;
  let configured = false;
  const range = position.bindRange();
  const set = (left: number): void => position.set(left);
  const sync = (): void => {
    if (!configured) return;
    applying = true;
    try {
      scrollable.setScrollPosition({ scrollLeft: position.get() });
    } finally {
      applying = false;
    }
    scrollbar.setAttribute("aria-valuenow", String(position.get()));
  };
  const subscription = scrollable.onScroll((event) => {
    if (!applying && event.scrollLeftChanged) set(event.scrollLeft);
  });
  const unsubscribe = position.subscribe(sync);
  const keydown = (event: KeyboardEvent): void => {
    if (event.target !== scrollbar) return;
    const delta =
      event.key === "ArrowLeft"
        ? -40
        : event.key === "ArrowRight"
          ? 40
          : event.key === "PageUp"
            ? -viewportWidth
            : event.key === "PageDown"
              ? viewportWidth
              : 0;
    if (delta !== 0) set(position.get() + delta);
    else if (event.key === "Home") set(0);
    else if (event.key === "End") set(maximum);
    else return;
    event.preventDefault();
    event.stopPropagation();
  };
  node.addEventListener("keydown", keydown);
  return {
    move: (delta: number) => set(position.get() + delta),
    configure: (left: number, width: number, contentWidth: number, size: number) => {
      configured = true;
      maximum = Math.max(0, contentWidth - width);
      viewportWidth = width;
      height = size;
      node.style.left = `${left}px`;
      node.style.width = `${width}px`;
      node.style.height = `${size}px`;
      scrollable.updateOptions({ horizontalScrollbarSize: size });
      applying = true;
      try {
        scrollable.setScrollDimensions({ width, height: size, scrollWidth: contentWidth });
      } finally {
        applying = false;
      }
      range.update(maximum);
      sync();
      scrollbar.setAttribute("aria-valuemax", String(maximum));
    },
    place: (top: number, bottom: number) => {
      node.hidden = maximum === 0 || bottom <= Math.max(top, 0);
      const next = Math.max(0, bottom - height);
      if (next === previousTop) return;
      previousTop = next;
      node.style.top = `${next}px`;
    },
    dispose: () => {
      unsubscribe();
      subscription.dispose();
      range.dispose();
      node.removeEventListener("keydown", keydown);
      scrollable.dispose();
      node.remove();
    },
  };
}
