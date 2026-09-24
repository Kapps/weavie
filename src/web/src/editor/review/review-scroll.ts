import {
  getWindow,
  scheduleAtNextAnimationFrame,
} from "@codingame/monaco-vscode-api/vscode/vs/base/browser/dom";
import type { IMouseWheelEvent } from "@codingame/monaco-vscode-api/vscode/vs/base/browser/mouseEvent";
import { SmoothScrollableElement } from "@codingame/monaco-vscode-api/vscode/vs/base/browser/ui/scrollbar/scrollableElement";
import { ScrollbarVisibility } from "@codingame/monaco-vscode-api/vscode/vs/base/common/scrollable";
import { registerMiddleClickScroll } from "../../chrome/middle-click-scroll-surface";
import { currentEditorOptions, onEditorOptionsChanged } from "../../editor-options";
import { ReviewScrollState } from "./review-scroll-state";

// Match Monaco's ViewLayout animation duration.
const SMOOTH_SCROLL_MS = 125;

/** One scroll position drives file placement and Monaco viewports before the browser paints. */
export interface ReviewScroll {
  readonly element: HTMLElement;
  readonly viewport: HTMLElement;
  getScrollTop(): number;
  setScrollTop(top: number): void;
  setScrollAnchor(top: number, adjustment: number): void;
  setContentHeight(height: number): void;
  onScroll(listener: (userInitiated: boolean) => void): () => void;
  wheel(event: WheelEvent): void;
  dispose(): void;
}

export function createReviewScroll(element: HTMLElement, content: HTMLElement): ReviewScroll {
  const state = new ReviewScrollState({
    forceIntegerValues: false,
    smoothScrollDuration: currentEditorOptions().smoothScrolling ? SMOOTH_SCROLL_MS : 0,
    // Commit before Monaco's coordinated editor rendering (priority 100).
    scheduleAtNextAnimationFrame: (callback) =>
      scheduleAtNextAnimationFrame(getWindow(element), callback, 101),
  });
  const offOptions = onEditorOptionsChanged((options) => {
    state.setSmoothScrollDuration(options.smoothScrolling ? SMOOTH_SCROLL_MS : 0);
  });
  // The logical extent stays fixed while the painted list moves inside it.
  const extent = document.createElement("div");
  extent.className = "unified-review-scroll-extent";
  extent.appendChild(content);
  const scrollable = new SmoothScrollableElement(
    extent,
    {
      vertical: ScrollbarVisibility.Auto,
      horizontal: ScrollbarVisibility.Hidden,
      useShadows: false,
      alwaysConsumeMouseWheel: true,
      mouseWheelSmoothScroll: true,
      handleMouseWheel: false,
    },
    state,
  );
  const node = scrollable.getDomNode();
  const wheel = (event: WheelEvent): void => {
    state.wheel(() =>
      scrollable.delegateScrollFromMouseWheelEvent(event as WheelEvent & IMouseWheelEvent),
    );
  };
  node.addEventListener("wheel", wheel, { passive: false });
  node.style.overflow = "clip";
  extent.style.overflow = "visible";
  element.appendChild(node);
  content.id = `review-scroll-${crypto.randomUUID()}`;
  const scrollbar = node.querySelector<HTMLElement>(":scope > .scrollbar.vertical")!;
  scrollbar.removeAttribute("aria-hidden");
  scrollbar.setAttribute("role", "scrollbar");
  scrollbar.setAttribute("aria-label", "Review scroll position");
  scrollbar.setAttribute("aria-orientation", "vertical");
  scrollbar.setAttribute("aria-controls", content.id);
  scrollbar.setAttribute("aria-valuemin", "0");
  scrollbar.tabIndex = 0;
  const listeners = new Set<(userInitiated: boolean) => void>();
  let updating = false;
  const update = (change: () => void): void => {
    const previous = updating;
    updating = true;
    try {
      change();
    } finally {
      updating = previous;
    }
  };
  let contentHeight = 0;
  let viewportWidth = 0;
  let viewportHeight = 0;
  const getScrollTop = (): number => scrollable.getScrollPosition().scrollTop;
  const setScrollTop = (scrollTop: number): void => {
    update(() => scrollable.setScrollPosition({ scrollTop }));
  };
  const offMiddleClick = registerMiddleClickScroll(node, () => true, {
    x: null,
    y: (delta) =>
      scrollable.setScrollPosition({
        scrollTop: state.getFutureScrollPosition().scrollTop + delta,
      }),
  });
  const render = (): void => {
    const top = getScrollTop();
    content.style.transform = `translateY(${-top}px)`;
    scrollbar.setAttribute("aria-valuenow", String(top));
    const dimensions = scrollable.getScrollDimensions();
    scrollbar.setAttribute(
      "aria-valuemax",
      String(Math.max(0, dimensions.scrollHeight - dimensions.height)),
    );
    const userInitiated = !updating;
    for (const listener of listeners) listener(userInitiated);
  };
  const layout = (): void => {
    update(() =>
      scrollable.setScrollDimensions({
        width: viewportWidth,
        height: viewportHeight,
        scrollHeight: contentHeight,
      }),
    );
  };
  const subscription = scrollable.onScroll(render);
  const observer = new ResizeObserver(([entry]) => {
    viewportWidth = entry!.contentRect.width;
    viewportHeight = entry!.contentRect.height;
    layout();
  });
  observer.observe(node);
  const keydown = (event: KeyboardEvent): void => {
    if (event.target !== scrollbar && event.target !== element) return;
    const dimensions = scrollable.getScrollDimensions();
    const page = dimensions.height;
    let top = state.getFutureScrollPosition().scrollTop;
    switch (event.key) {
      case "ArrowDown":
        top += 40;
        break;
      case "ArrowUp":
        top -= 40;
        break;
      case "PageDown":
        top += page;
        break;
      case "PageUp":
        top -= page;
        break;
      case "Home":
        top = 0;
        break;
      case "End":
        top = dimensions.scrollHeight;
        break;
      default:
        return;
    }
    event.preventDefault();
    event.stopPropagation();
    scrollable.setScrollPosition({ scrollTop: top });
  };
  const focus = (event: FocusEvent): void => {
    const target = event.target;
    if (
      !(target instanceof HTMLElement) ||
      !content.contains(target) ||
      target.closest(".monaco-editor")
    )
      return;
    const viewport = node.getBoundingClientRect();
    const rect = target.getBoundingClientRect();
    const delta =
      rect.top < viewport.top
        ? rect.top - viewport.top
        : Math.max(0, rect.bottom - viewport.bottom);
    if (delta !== 0) setScrollTop(getScrollTop() + delta);
  };
  element.addEventListener("focusin", focus);
  element.addEventListener("keydown", keydown);
  viewportWidth = node.clientWidth;
  viewportHeight = node.clientHeight;
  layout();
  render();
  return {
    element,
    viewport: node,
    getScrollTop,
    setScrollTop,
    setScrollAnchor: (top, adjustment) => update(() => state.setScrollAnchor(top, adjustment)),
    setContentHeight: (height) => {
      if (contentHeight === height) return;
      contentHeight = height;
      extent.style.height = `${height}px`;
      layout();
    },
    onScroll: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    wheel,
    dispose: () => {
      offMiddleClick();
      offOptions();
      observer.disconnect();
      subscription.dispose();
      node.removeEventListener("wheel", wheel);
      element.removeEventListener("keydown", keydown);
      element.removeEventListener("focusin", focus);
      listeners.clear();
      scrollable.dispose();
      state.dispose();
      // Solid removes the DOM after the tab captures its geometry during cleanup.
    },
  };
}
