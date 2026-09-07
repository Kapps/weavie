import { monaco } from "../monaco-setup";

/** Keeps Monaco's rendered window inside a full-height section owned by the review scroller. */
export function createReviewEditorViewport(
  container: HTMLElement,
  mount: HTMLElement,
  scroller: HTMLElement,
  header: HTMLElement,
  editor: monaco.editor.IStandaloneCodeEditor,
): { layout(): void; update(change: () => void): void; dispose(): void } {
  let frame: number | undefined;
  let syncing = false;

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
      const height = Math.min(container.clientHeight, viewport.height);
      const offset = viewport.top - container.getBoundingClientRect().top;
      const top = Math.max(0, Math.min(offset, container.clientHeight - height));
      mount.style.top = `${top}px`;
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
        layout();
      });
    }
  };
  const observer = new ResizeObserver(schedule);
  observer.observe(scroller);
  observer.observe(container);
  observer.observe(header);
  scroller.addEventListener("scroll", schedule, { passive: true });
  // Keyboard/caret reveals still move the page; only viewport synchronization may scroll Monaco alone.
  const scroll = editor.onDidScrollChange((event) => {
    if (!syncing && event.scrollTopChanged) {
      scroller.scrollTop += container.getBoundingClientRect().top - bounds().top + event.scrollTop;
      layout();
    }
  });
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
    layout,
    update: (change) => {
      syncing = true;
      try {
        change();
        layout();
      } finally {
        syncing = false;
      }
    },
    dispose: () => {
      if (frame !== undefined) {
        cancelAnimationFrame(frame);
      }
      observer.disconnect();
      scroller.removeEventListener("scroll", schedule);
      mount.removeEventListener("wheel", wheel);
      scroll.dispose();
    },
  };
}
