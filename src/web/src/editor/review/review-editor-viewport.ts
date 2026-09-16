import { monaco } from "../monaco-setup";
import { createReviewEditorInput } from "./review-editor-input";

/** Keeps a bounded buffer of painted lines inside a full-height section owned by the review scroller. */
export function createReviewEditorViewport(
  container: HTMLElement,
  mount: HTMLElement,
  scroller: HTMLElement,
  header: HTMLElement,
  editor: monaco.editor.IStandaloneCodeEditor,
): {
  bounds(): { top: number; height: number };
  layout(): void;
  reveal(top: number): void;
  update(change: () => void): void;
  dispose(): void;
} {
  let frame: number | undefined;
  let syncing = false;
  let windowTop = 0;
  let scrollbarOffset = Number.NaN;

  const bounds = (): { top: number; height: number } => {
    const style = getComputedStyle(scroller);
    const paddingTop = Number.parseFloat(style.paddingTop);
    const inset = paddingTop + header.offsetHeight;
    return {
      top: scroller.getBoundingClientRect().top + scroller.clientTop + inset,
      height: Math.max(0, scroller.clientHeight - inset),
    };
  };

  const layout = (): void => {
    const wasSyncing = syncing;
    syncing = true;
    try {
      const viewport = bounds();
      const height = Math.min(
        editor.getContentHeight(),
        container.clientHeight,
        viewport.height * 3,
      );
      const width = container.clientWidth;
      const previous = editor.getLayoutInfo();
      const resized = previous.width !== width || previous.height !== height;
      if (resized) editor.layout({ width, height });
      const containerTop = container.getBoundingClientRect().top;
      const visibleTop = Math.max(0, viewport.top - containerTop);
      const maximumTop = Math.max(0, editor.getScrollHeight() - height);
      // Refill before the visible page reaches an edge; small reversals keep the same painted lines.
      const runway = viewport.height / 2;
      if (
        resized ||
        windowTop > maximumTop ||
        visibleTop < windowTop + runway ||
        visibleTop + viewport.height > windowTop + height - runway
      ) {
        windowTop = Math.floor(Math.max(0, Math.min(visibleTop - viewport.height, maximumTop)));
      }
      const top = windowTop;
      const moved = editor.getScrollTop() !== top;
      if (mount.style.top !== `${top}px`) mount.style.top = `${top}px`;
      if (moved) editor.setScrollTop(top, monaco.editor.ScrollType.Immediate);
      if (resized || moved) editor.render();
      const offset = Math.max(
        editor.getLayoutInfo().horizontalScrollbarHeight - height,
        Math.min(0, viewport.top + viewport.height - containerTop - top - height),
      );
      if (scrollbarOffset !== offset) {
        scrollbarOffset = offset;
        mount.style.setProperty("--review-scrollbar-offset", `${offset}px`);
      }
    } finally {
      syncing = wasSyncing;
    }
  };
  const schedule = (): void => {
    if (frame === undefined) {
      frame = requestAnimationFrame(() => {
        frame = undefined;
        layout();
      });
    }
  };
  const observer = new ResizeObserver(schedule);
  observer.observe(scroller);
  observer.observe(container);
  observer.observe(header);
  scroller.addEventListener("scroll", schedule, { passive: true });
  const reveal = (top: number): void => {
    scroller.scrollTop += container.getBoundingClientRect().top - bounds().top + top;
    layout();
  };
  const input = createReviewEditorInput({
    editor,
    container,
    scroller,
    bounds,
    isSyncing: () => syncing,
    schedule,
  });
  const scroll = editor.onDidScrollChange((event) => {
    if (!syncing && event.scrollTopChanged && !event.scrollHeightChanged) {
      input.scrollChanged(event.scrollTop, windowTop);
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
      const keepCursorVisible = !wasSyncing && input.isCursorVisible();
      syncing = true;
      try {
        change();
      } finally {
        syncing = wasSyncing;
      }
      if (keepCursorVisible) input.revealCursor();
      layout();
    },
    dispose: () => {
      if (frame !== undefined) {
        cancelAnimationFrame(frame);
      }
      observer.disconnect();
      scroller.removeEventListener("scroll", schedule);
      mount.removeEventListener("wheel", wheel, { capture: true });
      scroll.dispose();
      input.dispose();
    },
  };
}
