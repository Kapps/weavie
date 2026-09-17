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
  bounds(): { top: number; height: number };
  layout(): void;
  reveal(top: number): void;
  update(change: () => void): void;
  dispose(): void;
} {
  const scroller = scrollOwner.viewport;
  let syncing = false;

  const bounds = (): { top: number; height: number } => {
    const headerHeight = header.getBoundingClientRect().height;
    return {
      top: scroller.getBoundingClientRect().top + headerHeight,
      height: Math.max(0, scroller.clientHeight - headerHeight),
    };
  };

  const layout = (): void => {
    const wasSyncing = syncing;
    syncing = true;
    try {
      const viewport = bounds();
      const containerTop = container.getBoundingClientRect().top;
      const contentHeight = Math.min(editor.getContentHeight(), container.clientHeight);
      const top = Math.min(contentHeight, Math.max(0, Math.ceil(viewport.top - containerTop)));
      const height = Math.max(
        0,
        Math.floor(
          Math.min(viewport.top + viewport.height, containerTop + contentHeight) -
            (containerTop + top),
        ),
      );
      const width = container.clientWidth;
      const previous = editor.getLayoutInfo();
      const resized = previous.width !== width || previous.height !== height;
      if (resized) editor.layout({ width, height });
      const moved = editor.getScrollTop() !== top;
      if (mount.style.top !== `${top}px`) mount.style.top = `${top}px`;
      if (moved) editor.setScrollTop(top, monaco.editor.ScrollType.Immediate);
    } finally {
      syncing = wasSyncing;
    }
  };
  const observer = new ResizeObserver(layout);
  observer.observe(scroller);
  observer.observe(container);
  observer.observe(header);
  const unsubscribe = scrollOwner.onScroll(layout);
  const reveal = (top: number): void => {
    scrollOwner.setScrollTop(
      scrollOwner.getScrollTop() + container.getBoundingClientRect().top - bounds().top + top,
    );
    layout();
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
      try {
        change();
        layout();
      } finally {
        syncing = wasSyncing;
      }
    },
    dispose: () => {
      observer.disconnect();
      unsubscribe();
      mount.removeEventListener("wheel", wheel, { capture: true });
      scroll.dispose();
    },
  };
}
