import type { IActiveCodeEditor } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/editorBrowser";
import { ViewEventHandler } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewEventHandler";
import {
  VerticalRevealType,
  type ViewRevealRangeRequestEvent,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewEvents";
import { Viewport } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewModel";
import { monaco } from "../monaco-setup";
import { registerReviewEditorCommands } from "./review-editor-commands";

/** Keeps keyboard navigation relative to the visible page, outside Monaco's render buffer. */
export function createReviewEditorInput(options: {
  editor: monaco.editor.IStandaloneCodeEditor;
  container: HTMLElement;
  scroller: HTMLElement;
  bounds: () => { top: number; height: number };
  isSyncing: () => boolean;
  schedule: () => void;
  renderReveal: () => void;
}): {
  revealCursor(): void;
  isCursorVisible(): boolean;
  scrollChanged(scrollTop: number, windowTop: number): void;
  dispose(): void;
} {
  const { editor, container, scroller } = options;
  const visibleHeight = (): number =>
    options.bounds().height - editor.getLayoutInfo().horizontalScrollbarHeight;
  const cursorBounds = (): { top: number; bottom: number } | undefined => {
    if (!editor.hasWidgetFocus()) return;
    const position = editor.getPosition();
    if (position === null) return;
    const top =
      container.getBoundingClientRect().top +
      editor.getTopForPosition(position.lineNumber, position.column);
    return { top, bottom: top + editor.getOption(monaco.editor.EditorOption.lineHeight) };
  };
  const isCursorVisible = (): boolean => {
    const cursor = cursorBounds();
    const viewport = options.bounds();
    return (
      cursor !== undefined &&
      cursor.top >= viewport.top &&
      cursor.bottom <= viewport.top + visibleHeight()
    );
  };
  const revealRange = (top: number, bottom: number, type: VerticalRevealType): void => {
    const viewport = options.bounds();
    const height = visibleHeight();
    const outside = top < viewport.top || bottom > viewport.top + height;
    if (
      !outside &&
      (type === VerticalRevealType.CenterIfOutsideViewport ||
        type === VerticalRevealType.NearTopIfOutsideViewport)
    )
      return;
    let delta = 0;
    switch (type) {
      case VerticalRevealType.CenterIfOutsideViewport:
      case VerticalRevealType.NearTopIfOutsideViewport:
      case VerticalRevealType.Center:
      case VerticalRevealType.NearTop:
        delta =
          type === VerticalRevealType.Center || type === VerticalRevealType.CenterIfOutsideViewport
            ? (top + bottom - height) / 2 - viewport.top
            : Math.max(
                bottom - height,
                top -
                  Math.max(
                    5 * editor.getOption(monaco.editor.EditorOption.lineHeight),
                    height * 0.2,
                  ),
              ) - viewport.top;
        break;
      case VerticalRevealType.Top:
        delta = top - viewport.top;
        break;
      case VerticalRevealType.Bottom:
        delta = bottom - viewport.top - height;
        break;
      default:
        delta =
          top < viewport.top ? top - viewport.top : Math.max(0, bottom - viewport.top - height);
    }
    if (delta !== 0) scroller.scrollTop += delta;
  };
  const revealCursor = (): void => {
    if (options.isSyncing()) return;
    const cursor = cursorBounds();
    if (cursor === undefined) return;
    revealRange(cursor.top, cursor.bottom, VerticalRevealType.Simple);
    options.schedule();
  };
  let disposed = false;
  let queued = false;
  let scrollDelta = 0;
  let requested: { top: number; bottom: number; type: VerticalRevealType } | null | undefined;
  const enqueue = (): void => {
    if (queued) return;
    queued = true;
    queueMicrotask(() => {
      queued = false;
      if (disposed) return;
      if (requested === undefined) {
        scroller.scrollTop += scrollDelta;
      }
      requested = undefined;
      scrollDelta = 0;
      options.schedule();
    });
  };
  const internal = editor as unknown as IActiveCodeEditor;
  let model = internal._getViewModel();
  const handler = new (class extends ViewEventHandler {
    override onRevealRangeRequest(event: ViewRevealRangeRequestEvent): boolean {
      if (options.isSyncing()) return false;
      const ranges = event.range === null ? event.selections : [event.range];
      if (ranges === null || ranges.length === 0) return false;
      requested = {
        top: model.viewLayout.getVerticalOffsetForLineNumber(
          Math.min(...ranges.map((range) => range.startLineNumber)),
        ),
        bottom:
          model.viewLayout.getVerticalOffsetForLineNumber(
            Math.max(...ranges.map((range) => range.endLineNumber)),
          ) + editor.getOption(monaco.editor.EditorOption.lineHeight),
        type: event.verticalType,
      };
      if (requested.bottom - requested.top > visibleHeight()) {
        if (event.range === null) requested = null;
        else requested.type = VerticalRevealType.Top;
      }
      if (requested !== null) {
        const offset = container.getBoundingClientRect().top;
        revealRange(offset + requested.top, offset + requested.bottom, requested.type);
        options.renderReveal();
      }
      enqueue();
      return false;
    }
  })();
  model.addViewEventHandler(handler);
  const subscriptions = [
    editor.onDidChangeModel(() => {
      model.removeViewEventHandler(handler);
      model = internal._getViewModel();
      model.addViewEventHandler(handler);
      requested = undefined;
      scrollDelta = 0;
    }),
  ];
  const physicalTop = (): number =>
    Math.max(0, options.bounds().top - container.getBoundingClientRect().top);
  const commandModel = (): typeof model => {
    const viewLayout = new Proxy(model.viewLayout, {
      get(target, key, receiver) {
        if (key === "getCurrentScrollTop") return physicalTop;
        if (key === "getCurrentViewport" || key === "getFutureViewport")
          return () => {
            const current = target.getCurrentViewport();
            return new Viewport(physicalTop(), current.left, current.width, visibleHeight());
          };
        if (key === "getLinesViewportDataAtScrollTop")
          return (top: number) =>
            target.getLinesViewportData.call(
              new Proxy(viewLayout, {
                get(layout, option, layoutReceiver) {
                  if (option === "getCurrentViewport")
                    return () => {
                      const current = target.getCurrentViewport();
                      return new Viewport(
                        Math.max(0, Math.min(top, target.getScrollHeight() - visibleHeight())),
                        current.left,
                        current.width,
                        visibleHeight(),
                      );
                    };
                  return Reflect.get(layout, option, layoutReceiver);
                },
              }),
            );
        if (key === "setScrollPosition")
          return (position: { scrollTop?: number; scrollLeft?: number }) => {
            if (position.scrollLeft !== undefined) editor.setScrollLeft(position.scrollLeft);
            if (position.scrollTop !== undefined)
              scroller.scrollTop += position.scrollTop - physicalTop();
            options.schedule();
          };
        return Reflect.get(target, key, receiver);
      },
    });
    return new Proxy(model, {
      get(target, key, receiver) {
        if (key === "viewLayout") return viewLayout;
        if (key === "cursorConfig")
          return new Proxy(target.cursorConfig, {
            get(config, option, configReceiver) {
              return option === "pageSize"
                ? Math.max(1, Math.floor(visibleHeight() / config.lineHeight) - 2)
                : Reflect.get(config, option, configReceiver);
            },
          });
        return Reflect.get(target, key, receiver);
      },
    });
  };
  subscriptions.push(registerReviewEditorCommands(internal, commandModel));
  return {
    revealCursor,
    isCursorVisible,
    scrollChanged: (scrollTop, windowTop) => {
      if (options.isSyncing()) return;
      scrollDelta = scrollTop - windowTop;
      enqueue();
    },
    dispose: () => {
      disposed = true;
      model.removeViewEventHandler(handler);
      handler.dispose();
      for (const subscription of subscriptions) subscription.dispose();
    },
  };
}
