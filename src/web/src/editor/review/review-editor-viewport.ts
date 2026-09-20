import { monaco } from "../monaco-setup";
import type { ReviewScroll } from "./review-scroll";

/** Keeps Monaco's rendered window inside a full-height section owned by the review scroller. */
export function createReviewEditorViewport(
  container: HTMLElement,
  mount: HTMLElement,
  scrollOwner: ReviewScroll,
  header: HTMLElement,
  editor: monaco.editor.IStandaloneCodeEditor,
): {
  bounds(): { top: number; bottom: number; height: number };
  layout(): void;
  reveal(top: number): void;
  update(change: () => void): void;
  dispose(): void;
} {
  const scroller = scrollOwner.viewport;
  let disposed = false;
  let syncing = false;
  let updateDepth = 0;
  let containerTop = 0;
  let containerHeight = 0;
  let width = 0;
  let headerHeight = 0;
  let viewportHeight = 0;

  // Coordinates are local to the editor content; scrolling never needs a DOM measurement.
  const bounds = (): { top: number; bottom: number; height: number } => {
    const top = scrollOwner.getScrollTop() + headerHeight - containerTop;
    const height = Math.max(0, viewportHeight - headerHeight);
    return {
      top,
      bottom: Math.min(top + height, containerHeight, editor.getContentHeight()),
      height,
    };
  };

  const sync = (): void => {
    if (disposed || updateDepth !== 0) return;
    const wasSyncing = syncing;
    syncing = true;
    try {
      const viewport = bounds();
      const contentHeight = Math.min(editor.getContentHeight(), containerHeight);
      const top = Math.min(contentHeight, Math.max(0, Math.ceil(viewport.top)));
      const height = Math.max(0, Math.floor(viewport.bottom - top));
      const previous = editor.getLayoutInfo();
      const resized = previous.width !== width || previous.height !== height;
      // Let Monaco coordinate rendering after both the size and scroll position are updated.
      if (resized) editor.layout({ width, height }, true);
      const moved = editor.getScrollTop() !== top;
      if (mount.style.top !== `${top}px`) mount.style.top = `${top}px`;
      if (moved) editor.setScrollTop(top, monaco.editor.ScrollType.Immediate);
    } finally {
      syncing = wasSyncing;
    }
  };
  const layout = (): void => {
    if (disposed || updateDepth !== 0) return;
    containerTop =
      container.getBoundingClientRect().top -
      scroller.getBoundingClientRect().top +
      scrollOwner.getScrollTop();
    containerHeight = container.clientHeight;
    width = container.clientWidth;
    headerHeight = header.getBoundingClientRect().height;
    viewportHeight = scroller.clientHeight;
    sync();
  };
  const observer = new ResizeObserver(layout);
  observer.observe(scroller);
  observer.observe(container);
  observer.observe(header);
  const unsubscribe = scrollOwner.onScroll(sync);
  const reveal = (top: number): void => {
    if (disposed) return;
    scrollOwner.setScrollTop(containerTop - headerHeight + top);
    sync();
  };
  // Native editor navigation feeds the same scroll owner as wheel and scrollbar input.
  const scroll = editor.onDidScrollChange((event) => {
    if (!syncing && event.scrollTopChanged && !event.scrollHeightChanged) {
      reveal(event.scrollTop);
    }
  });
  const wheel = (event: WheelEvent): void => {
    const target = event.target;
    if (!(target instanceof Element)) return;
    const root = editor.getDomNode();
    const scrollable = target.closest(".monaco-scrollable-element");
    if (
      target.closest(".monaco-editor") !== root ||
      (scrollable !== null && scrollable !== root?.querySelector(".monaco-scrollable-element"))
    ) {
      return;
    }
    // The review owns root-editor scrolling; nested widgets keep Monaco's own wheel handling.
    event.stopPropagation();
    const horizontal = event.deltaX || (event.shiftKey ? event.deltaY : 0);
    if (horizontal === 0) {
      scrollOwner.wheel(event);
      return;
    }
    const unit =
      event.deltaMode === WheelEvent.DOM_DELTA_LINE
        ? editor.getOption(monaco.editor.EditorOption.lineHeight)
        : event.deltaMode === WheelEvent.DOM_DELTA_PAGE
          ? editor.getLayoutInfo().width
          : 1;
    editor.setScrollLeft(editor.getScrollLeft() + horizontal * unit);
    if (event.shiftKey || event.deltaY === 0) {
      event.preventDefault();
    } else {
      scrollOwner.wheel(event);
    }
  };
  mount.addEventListener("wheel", wheel, { capture: true, passive: false });
  layout();
  return {
    bounds,
    layout,
    reveal,
    update: (change) => {
      const wasSyncing = syncing;
      syncing = true;
      updateDepth += 1;
      try {
        change();
      } finally {
        updateDepth -= 1;
        try {
          if (updateDepth === 0) layout();
        } finally {
          syncing = wasSyncing;
        }
      }
    },
    dispose: () => {
      disposed = true;
      observer.disconnect();
      unsubscribe();
      mount.removeEventListener("wheel", wheel, { capture: true });
      scroll.dispose();
    },
  };
}
