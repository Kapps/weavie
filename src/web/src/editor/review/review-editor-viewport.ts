import { monaco } from "../monaco-setup";
import type { ReviewScroll } from "./review-scroll";

export interface ReviewSectionGeometry {
  readonly element: HTMLElement;
  top(): number;
}

/** Keeps Monaco's rendered window inside a full-height section owned by the review scroller. */
export function createReviewEditorViewport(
  container: HTMLElement,
  mount: HTMLElement,
  scrollOwner: ReviewScroll,
  header: HTMLElement,
  editor: monaco.editor.IStandaloneCodeEditor,
  section: ReviewSectionGeometry,
): {
  bounds(): { top: number; bottom: number; height: number };
  layout(): void;
  position(): void;
  setContentHeight(height: number): void;
  reveal(top: number): void;
  update(change: () => void): void;
  dispose(): void;
} {
  const scroller = scrollOwner.viewport;
  let syncing = false;
  let disposed = false;
  let updateDepth = 0;
  let dirty = false;
  let containerOffset = 0;
  let sectionTop = section.top();
  let containerHeight = 0;
  let width = 0;
  let headerHeight = 0;
  let viewportHeight = 0;
  let cursorVisible = false;

  // Coordinates are local to the editor content; scrolling never needs a DOM measurement.
  const bounds = (): { top: number; bottom: number; height: number } => {
    const top = scrollOwner.getScrollTop() + headerHeight - sectionTop - containerOffset;
    const height = Math.max(0, viewportHeight - headerHeight);
    return {
      top,
      bottom: Math.min(top + height, containerHeight, editor.getContentHeight()),
      height,
    };
  };

  const rememberCursorVisibility = (): void => {
    const position = editor.getPosition();
    const viewport = bounds();
    const top =
      position === null
        ? -Infinity
        : editor.getTopForPosition(position.lineNumber, position.column);
    cursorVisible =
      top >= viewport.top &&
      top + editor.getOption(monaco.editor.EditorOption.lineHeight) <= viewport.bottom;
  };
  const sync = (): void => {
    if (disposed) return;
    dirty = true;
    if (syncing || updateDepth > 0) return;
    syncing = true;
    try {
      while (dirty) {
        dirty = false;
        const viewport = bounds();
        const contentHeight = Math.min(editor.getContentHeight(), containerHeight);
        const height = Math.max(0, Math.floor(Math.min(contentHeight, viewport.height)));
        const top = Math.max(0, Math.min(Math.ceil(viewport.top), contentHeight - height));
        const previous = editor.getLayoutInfo();
        const resized = previous.width !== width || previous.height !== height;
        // Let Monaco coordinate rendering after both the size and scroll position are updated.
        if (resized) editor.layout({ width, height }, true);
        const moved = editor.getScrollTop() !== top;
        if (moved) editor.setScrollTop(top, monaco.editor.ScrollType.Immediate);
        const offset = `${editor.getScrollTop()}px`;
        if (mount.style.top !== offset) mount.style.top = offset;
      }
    } finally {
      syncing = false;
    }
  };
  const layout = (): void => {
    if (disposed) return;
    containerOffset =
      container.getBoundingClientRect().top - section.element.getBoundingClientRect().top;
    containerHeight = container.clientHeight;
    width = container.clientWidth;
    headerHeight = header.getBoundingClientRect().height;
    viewportHeight = scroller.clientHeight;
    sync();
    rememberCursorVisibility();
  };
  const observer = new ResizeObserver(layout);
  observer.observe(scroller);
  observer.observe(container);
  observer.observe(header);
  const unsubscribe = scrollOwner.onScroll(() => {
    sync();
    rememberCursorVisibility();
  });
  const reveal = (top: number): void => {
    scrollOwner.setScrollTop(sectionTop + containerOffset - headerHeight + top);
    sync();
  };
  // Native editor navigation feeds the same scroll owner as wheel and scrollbar input.
  const scroll = editor.onDidScrollChange((event) => {
    if (!syncing && updateDepth === 0 && event.scrollTopChanged && !event.scrollHeightChanged) {
      if (editor.hasWidgetFocus()) reveal(event.scrollTop);
      else sync();
    }
  });
  const revealCursor = (): void => {
    if (disposed || syncing || updateDepth > 0 || !editor.hasWidgetFocus()) return;
    const position = editor.getPosition();
    if (position === null) return;
    const viewport = bounds();
    const top = editor.getTopForPosition(position.lineNumber, position.column);
    const bottom = top + editor.getOption(monaco.editor.EditorOption.lineHeight);
    const delta = top < viewport.top ? top - viewport.top : Math.max(0, bottom - viewport.bottom);
    if (delta !== 0) scrollOwner.setScrollTop(scrollOwner.getScrollTop() + delta);
  };
  const cursor = editor.onDidChangeCursorPosition((event) => {
    if (event.source !== "model") revealCursor();
    rememberCursorVisibility();
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
    position: () => {
      if (disposed) return;
      sectionTop = section.top();
      sync();
      rememberCursorVisibility();
    },
    setContentHeight: (height) => {
      if (containerHeight === height) return;
      const preserveCursor = cursorVisible;
      containerHeight = height;
      sync();
      if (preserveCursor) revealCursor();
      rememberCursorVisibility();
    },
    reveal,
    update: (change) => {
      updateDepth++;
      try {
        change();
      } finally {
        updateDepth--;
        sync();
      }
    },
    dispose: () => {
      disposed = true;
      observer.disconnect();
      unsubscribe();
      mount.removeEventListener("wheel", wheel, { capture: true });
      scroll.dispose();
      cursor.dispose();
    },
  };
}
