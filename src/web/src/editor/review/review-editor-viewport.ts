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

  const bounds = (): { top: number; height: number } => {
    const style = getComputedStyle(scroller);
    const paddingTop = Number.parseFloat(style.paddingTop);
    const inset = paddingTop + header.offsetHeight;
    return {
      top: scroller.getBoundingClientRect().top + scroller.clientTop + inset,
      height: Math.max(0, scroller.clientHeight - inset),
    };
  };

  const projectedTop = (): number =>
    Math.floor(
      Math.max(
        0,
        Math.min(
          bounds().top - container.getBoundingClientRect().top,
          editor.getScrollHeight() - editor.getLayoutInfo().height,
        ),
      ),
    );

  const layout = (): void => {
    const wasSyncing = syncing;
    syncing = true;
    try {
      const viewport = bounds();
      const height = Math.min(editor.getContentHeight(), viewport.height);
      editor.layout({ width: container.clientWidth, height });
      const top = projectedTop();
      mount.style.top = `${top}px`;
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
  const reveal = (top: number): void => {
    scroller.scrollTop += container.getBoundingClientRect().top - bounds().top + top;
    layout();
  };
  // Keyboard/caret reveals still move the page; only viewport synchronization may scroll Monaco alone.
  const scroll = editor.onDidScrollChange((event) => {
    if (!syncing && event.scrollTopChanged && event.scrollTop !== projectedTop()) {
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
      mount.removeEventListener("wheel", wheel, { capture: true });
      scroll.dispose();
    },
  };
}
