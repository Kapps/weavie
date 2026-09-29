import {
  DisposableStore,
  toDisposable,
} from "@codingame/monaco-vscode-api/vscode/vs/base/common/lifecycle";
import { registerMiddleClickScroll } from "../../chrome/middle-click-scroll-surface";
import { monaco } from "../monaco-setup";
import type { ReviewScroll } from "./review-scroll";
import { routeReviewWheel } from "./review-wheel";

/** Keeps Monaco's rendered window inside a full-height section owned by the review scroller. */
export function createReviewEditorViewport(
  container: HTMLElement,
  mount: HTMLElement,
  widgets: HTMLElement,
  scrollOwner: ReviewScroll,
  header: HTMLElement,
  createEditor: (dimension: monaco.editor.IDimension) => monaco.editor.IStandaloneCodeEditor,
): {
  editor: monaco.editor.IStandaloneCodeEditor;
  bounds(): { top: number; bottom: number; height: number };
  layout(): void;
  shift(delta: number): void;
  reveal(top: number): void;
  update(change: () => void): void;
  dispose(): void;
} {
  const scroller = scrollOwner.viewport;
  const widgetHost = widgets.parentElement;
  let disposed = false;
  let syncing = false;
  let updateDepth = 0;
  let containerTop = 0;
  let containerHeight = 0;
  let width = 0;
  let headerHeight = 0;
  let viewportHeight = 0;
  const measure = (): void => {
    const containerBounds = container.getBoundingClientRect();
    const scrollerBounds = scroller.getBoundingClientRect();
    containerTop = containerBounds.top - scrollerBounds.top + scrollOwner.getScrollTop();
    containerHeight = container.clientHeight;
    width = container.clientWidth;
    headerHeight = header.getBoundingClientRect().height;
    viewportHeight = scroller.clientHeight;
  };
  // The absolute editor mount cannot change the reserved section geometry.
  measure();
  let dimension = { width, height: 0 };
  const editor = createEditor(dimension);
  const resources = new DisposableStore();
  const release = (): void => {
    if (disposed) return;
    disposed = true;
    resources.dispose();
  };
  try {
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
        {
          const outside = viewport.bottom <= 0 || viewport.top >= containerHeight;
          const focused =
            editor.hasWidgetFocus() ||
            mount.contains(document.activeElement) ||
            widgets.contains(document.activeElement);
          if (outside && !focused) {
            if (dimension.width !== width) {
              dimension = { width, height: dimension.height };
              editor.layout(dimension, true);
            }
            if (mount.isConnected) mount.remove();
            if (widgets.isConnected) widgets.remove();
            return;
          }
          if (!mount.isConnected) {
            container.append(mount);
            widgetHost!.append(widgets);
            editor.renderAsync(true);
          }
        }
        const contentHeight = Math.min(editor.getContentHeight(), containerHeight);
        const top = Math.min(contentHeight, Math.max(0, Math.ceil(viewport.top)));
        // Round the visible extent independently: fractional scrolling must not resize an interior band.
        const height = Math.max(
          0,
          Math.floor(Math.min(viewport.height + Math.min(viewport.top, 0), contentHeight - top)),
        );
        const resized = dimension.width !== width || dimension.height !== height;
        // Let Monaco coordinate rendering after both the size and scroll position are updated.
        if (resized) {
          // Monaco clamps an offscreen zero-height request; compare requests, not its clamped result.
          dimension = { width, height };
          editor.layout(dimension, true);
        }
        const moved = editor.getScrollTop() !== top;
        if (mount.style.top !== `${top}px`) mount.style.top = `${top}px`;
        if (moved) editor.setScrollTop(top, monaco.editor.ScrollType.Immediate);
      } finally {
        syncing = wasSyncing;
      }
    };
    const layout = (): void => {
      if (disposed || updateDepth !== 0) return;
      measure();
      sync();
    };
    const observer = new ResizeObserver(layout);
    resources.add(toDisposable(() => observer.disconnect()));
    observer.observe(scroller);
    observer.observe(container);
    observer.observe(header);
    resources.add(toDisposable(scrollOwner.onScroll(sync)));
    const reveal = (top: number): void => {
      if (disposed) return;
      scrollOwner.setScrollTop(containerTop - headerHeight + top);
      sync();
    };
    // Native editor navigation feeds the same scroll owner as wheel and scrollbar input.
    resources.add(
      editor.onDidScrollChange((event) => {
        if (!syncing && event.scrollTopChanged && !event.scrollHeightChanged) {
          reveal(event.scrollTop);
        }
      }),
    );
    const ownsTarget = (target: Element): boolean => {
      const root = editor.getDomNode();
      const scrollable = target.closest(".monaco-scrollable-element");
      return (
        root !== null &&
        target.closest(".monaco-editor") === root &&
        (scrollable === null ||
          !root.contains(scrollable) ||
          scrollable === root.querySelector(".monaco-scrollable-element"))
      );
    };
    resources.add(
      toDisposable(
        registerMiddleClickScroll(editor.getDomNode()!, ownsTarget, {
          x: (delta) => editor.setScrollLeft(editor.getScrollLeft() + delta),
          y: null,
        }),
      ),
    );
    const wheel = (event: WheelEvent): void => {
      const target = event.target;
      if (!(target instanceof Element) || !ownsTarget(target)) return;
      routeReviewWheel(
        event,
        scrollOwner,
        editor.getOption(monaco.editor.EditorOption.lineHeight),
        editor.getLayoutInfo().width,
        (delta) => editor.setScrollLeft(editor.getScrollLeft() + delta),
      );
    };
    resources.add(toDisposable(() => mount.removeEventListener("wheel", wheel, { capture: true })));
    mount.addEventListener("wheel", wheel, { capture: true, passive: false });
    sync();
    return {
      editor,
      bounds,
      layout,
      shift: (delta) => {
        if (disposed) return;
        containerTop += delta;
        sync();
      },
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
      dispose: release,
    };
  } catch (error) {
    try {
      resources.add(editor);
      release();
    } catch (cleanupError) {
      throw new AggregateError(
        [error, cleanupError],
        "Review viewport construction and cleanup failed",
      );
    }
    throw error;
  }
}
