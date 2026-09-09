import { monaco } from "../monaco-setup";

/** Keeps Monaco's rendered window inside a full-height section owned by the review scroller. */
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
  let renderedTop = 0;
  let selecting = false;
  let handlingInput = false;

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
      const overscan = selecting || handlingInput ? 0 : viewport.height;
      const height = Math.min(container.clientHeight, viewport.height + 2 * overscan);
      const offset = viewport.top - container.getBoundingClientRect().top;
      const top = Math.max(0, Math.min(offset - overscan, container.clientHeight - height));
      renderedTop = top;
      mount.style.top = `${top}px`;
      mount.style.setProperty("--review-viewport-top-offset", `${Math.max(0, offset - top)}px`);
      mount.style.setProperty(
        "--review-scrollbar-offset",
        `${Math.min(0, offset + viewport.height - top - height)}px`,
      );
      editor.layout({ width: container.clientWidth, height });
      editor.setScrollTop(top, monaco.editor.ScrollType.Immediate);
      editor.render();
    } finally {
      syncing = wasSyncing;
    }
  };
  const schedule = (): void => {
    if (frame === undefined) {
      frame = requestAnimationFrame(() => {
        frame = undefined;
        handlingInput = false;
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
  // Keyboard/caret reveals still move the page; only viewport synchronization may scroll Monaco alone.
  const scroll = editor.onDidScrollChange((event) => {
    if (!syncing && event.scrollTopChanged) {
      if (selecting || handlingInput) reveal(event.scrollTop);
      else {
        scroller.scrollTop += event.scrollTop - renderedTop;
        layout();
      }
    }
  });
  // Native caret reveals, paging, and selection use the visible viewport during input.
  const prepareInput = (): void => {
    if (syncing) return;
    handlingInput = true;
    layout();
    schedule();
  };
  const key = editor.onKeyDown(prepareInput);
  const cursor = editor.onDidChangeCursorPosition(prepareInput);
  // Native drag autoscroll needs the visible edge, rather than the offscreen render window.
  const pointer = editor.onMouseDown(({ event, target }) => {
    if (!event.leftButton || target.position === null) return;
    selecting = true;
    layout();
  });
  const endSelection = (): void => {
    if (!selecting) return;
    selecting = false;
    schedule();
  };
  window.addEventListener("pointerup", endSelection);
  window.addEventListener("pointercancel", endSelection);
  window.addEventListener("blur", endSelection);
  const wheel = (event: WheelEvent): void => {
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
  mount.addEventListener("wheel", wheel, { passive: false });
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
      if (frame !== undefined) {
        cancelAnimationFrame(frame);
      }
      observer.disconnect();
      scroller.removeEventListener("scroll", schedule);
      mount.removeEventListener("wheel", wheel);
      window.removeEventListener("pointerup", endSelection);
      window.removeEventListener("pointercancel", endSelection);
      window.removeEventListener("blur", endSelection);
      scroll.dispose();
      key.dispose();
      cursor.dispose();
      pointer.dispose();
    },
  };
}
