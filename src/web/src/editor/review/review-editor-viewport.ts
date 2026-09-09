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

  // Where `mount` (Monaco's small rendered window) must sit within `container` for the current scroll to line
  // up with the scroller's visible pane. A plain DOM write, never a Monaco call — safe to run synchronously
  // even mid-dispatch of Monaco's own event, unlike the rest of `layout()` below.
  const position = (): { top: number; height: number } => {
    const viewport = bounds();
    const height = Math.min(container.clientHeight, viewport.height);
    const offset = viewport.top - container.getBoundingClientRect().top;
    const top = Math.max(0, Math.min(offset, container.clientHeight - height));
    mount.style.top = `${top}px`;
    return { top, height };
  };

  const layout = (): void => {
    const wasSyncing = syncing;
    syncing = true;
    try {
      const { top, height } = position();
      editor.layout({ width: container.clientWidth, height });
      editor.setScrollTop(top, monaco.editor.ScrollType.Immediate);
      editor.render();
    } finally {
      syncing = wasSyncing;
    }
  };
  // Re-syncs `mount`'s DOM position immediately (never stale), deferring only the Monaco-reentrant part of
  // `layout()` to the next frame. Without the immediate `position()`, a same-file jump made from outside the
  // editor (e.g. an omnibar commit) calls the editor's own `.focus()` right after this while `mount` still sits
  // at its pre-jump spot; the browser's native scroll-into-view-on-focus then drags `scroller.scrollTop` back
  // toward that stale spot, undoing the line above and corrupting whatever reads the scroll position next
  // (capture()'s reviewLine() included). See the unified-review-history.spec.ts "document symbols preview"
  // regression this fixed.
  const schedule = (): void => {
    position();
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
  // Keyboard/caret reveals still move the page; only Monaco's own render pass may be deferred. The
  // scroller.scrollTop update below is a plain DOM mutation, not a Monaco call, so it stays inline — a
  // reader (e.g. capture()'s reviewLine()) that runs synchronously right after a reveal must see the real,
  // current scroll position, or its viewport-membership check works off stale bounds and silently anchors
  // to the wrong line (corrupting nav history entries that later collapse by line proximity). Only `layout()`,
  // which calls back into Monaco (setScrollTop, render), is deferred to the next animation frame: Monaco
  // fires this event mid-dispatch of its own reveal command, and calling back into it synchronously from
  // there is reentrant — occasionally leaving Monaco's own render pass and ours racing under CI-runner
  // scheduling pressure. See the flake note in unified-review-scroll.spec.ts.
  const scroll = editor.onDidScrollChange((event) => {
    if (!syncing && event.scrollTopChanged) {
      scroller.scrollTop += container.getBoundingClientRect().top - bounds().top + event.scrollTop;
      schedule();
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
      scroll.dispose();
    },
  };
}
