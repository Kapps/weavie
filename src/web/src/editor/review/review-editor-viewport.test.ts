import { ScrollbarVisibility } from "@codingame/monaco-vscode-api/vscode/vs/base/common/scrollable";
import type { IEditorConfiguration } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/config/editorConfiguration";
import {
  ConfigurationChangedEvent,
  EditorOption,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/config/editorOptions";
import { ViewLayout } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewLayout/viewLayout";
import type { editor as MonacoEditor } from "monaco-editor";
import { describe, expect, it, onTestFinished, vi } from "vitest";
import { createReviewEditorViewport } from "./review-editor-viewport";
import type { ReviewScroll } from "./review-scroll";

vi.mock("../monaco-setup", async () => ({
  monaco: {
    editor: {
      ScrollType: { Immediate: 1 },
      EditorOption: (
        await import("@codingame/monaco-vscode-api/vscode/vs/editor/common/config/editorOptions")
      ).EditorOption,
    },
  },
}));

function fixture() {
  vi.stubGlobal("getComputedStyle", () => ({ paddingTop: "6" }));
  vi.stubGlobal(
    "requestAnimationFrame",
    vi.fn(() => 1),
  );
  vi.stubGlobal("cancelAnimationFrame", vi.fn());
  vi.stubGlobal(
    "ResizeObserver",
    class {
      observe() {}
      disconnect() {}
    },
  );
  const layoutInfo = {
    width: 716,
    contentWidth: 648,
    height: 562,
    verticalScrollbarWidth: 14,
    horizontalScrollbarHeight: 12,
  };
  const values = new Map<EditorOption, unknown>([
    [EditorOption.layoutInfo, layoutInfo],
    [EditorOption.padding, { top: 6, bottom: 18 }],
    [EditorOption.lineHeight, 22],
    [EditorOption.smoothScrolling, false],
    [EditorOption.scrollBeyondLastLine, false],
    [
      EditorOption.scrollbar,
      {
        horizontal: ScrollbarVisibility.Auto,
        horizontalScrollbarSize: 12,
        ignoreHorizontalScrollbarInContentHeight: true,
      },
    ],
    [EditorOption.wrappingInfo, { isViewportWrapping: false }],
    [EditorOption.fontInfo, { typicalHalfwidthCharacterWidth: 10 }],
    [EditorOption.scrollBeyondLastColumn, 5],
  ]);
  const view = new ViewLayout(
    {
      options: {
        get: (id: EditorOption) => {
          if (!values.has(id)) throw new Error(`Unexpected editor option ${id}`);
          return values.get(id);
        },
      },
    } as IEditorConfiguration,
    4_000,
    [],
    () => ({ dispose() {} }),
  );
  view.setMaxLineWidth(13_100);
  view.getScrollable().setScrollPositionNow({ scrollTop: view.getScrollHeight() });
  let contentHeight = view.getContentHeight();
  let rootTop = view.getCurrentScrollTop();
  const writes: number[] = [];
  const state = {
    duringLayout: () => {},
    containerHeight: () => contentHeight,
    containerOffset: 38,
    sectionTop: 0,
    sectionRemoved: false,
    cursorLine: 1,
    widgetFocus: true,
    zoneHeight: 0,
  };
  const measure = vi.fn();
  const scroller = {
    clientTop: 0,
    clientHeight: 606,
    get scrollTop() {
      return rootTop;
    },
    set scrollTop(top: number) {
      writes.push(top);
      rootTop = Math.max(0, Math.min(top, contentHeight - 562));
    },
    getBoundingClientRect: () => ({ top: 0 }),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  const container = {
    get clientHeight() {
      measure();
      return state.containerHeight();
    },
    clientWidth: 716,
    getBoundingClientRect: () => {
      measure();
      return { top: state.containerOffset - rootTop };
    },
  };
  const mount = {
    style: { top: "", setProperty: vi.fn() },
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  let cursorChanged = (_event: {
    position: { lineNumber: number; column: number };
    source: string;
  }) => {};
  const editor = {
    hasWidgetFocus: () => state.widgetFocus,
    getPosition: () => ({ lineNumber: state.cursorLine, column: 1 }),
    getTopForPosition: (line: number) => (line - 1) * 22 + 6 + state.zoneHeight,
    getOption: () => 22,
    onDidChangeCursorPosition: (listener: typeof cursorChanged) => {
      cursorChanged = listener;
      return { dispose() {} };
    },
    layout: vi.fn(({ width, height }: { width: number; height: number }) => {
      layoutInfo.width = width;
      layoutInfo.height = height;
      const changed: boolean[] = [];
      changed[EditorOption.layoutInfo] = true;
      view.onConfigurationChanged(new ConfigurationChangedEvent(changed));
      state.duringLayout();
    }),
    getContentHeight: () => view.getContentHeight(),
    getScrollHeight: () => view.getScrollHeight(),
    getScrollTop: () => view.getCurrentScrollTop(),
    getLayoutInfo: () => layoutInfo,
    setScrollTop: (top: number) => view.getScrollable().setScrollPositionNow({ scrollTop: top }),
    onDidScrollChange: view.onDidScroll,
    render: vi.fn(),
  };
  const listeners = new Set<(userInitiated: boolean) => void>();
  const owner: ReviewScroll = {
    element: scroller as unknown as HTMLElement,
    viewport: {
      ...scroller,
      clientHeight: 594,
      getBoundingClientRect: () => ({ top: 6 }),
    } as unknown as HTMLElement,
    getScrollTop: () => rootTop,
    setScrollTop: (top) => {
      scroller.scrollTop = top;
      for (const listener of listeners) listener(false);
    },
    setContentHeight: vi.fn(),
    onScroll: (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    wheel: vi.fn(),
    dispose: vi.fn(),
  };
  const viewport = createReviewEditorViewport(
    container as HTMLElement,
    mount as unknown as HTMLElement,
    owner,
    { getBoundingClientRect: () => ({ height: 32 }) } as HTMLElement,
    editor as unknown as MonacoEditor.IStandaloneCodeEditor,
    {
      element: { getBoundingClientRect: () => ({ top: 6 - rootTop }) } as HTMLElement,
      top: () => {
        if (state.sectionRemoved) throw new Error("Stale read from <Show>");
        return state.sectionTop;
      },
    },
  );
  // Monaco publishes its clamped scroll before this DOM height mirror receives content-size changes.
  const content = view.onDidContentSizeChange(() => {
    contentHeight = view.getContentHeight();
    rootTop = Math.min(rootTop, contentHeight - 562);
  });
  onTestFinished(() => {
    viewport.dispose();
    content.dispose();
    view.dispose();
    vi.unstubAllGlobals();
  });
  return {
    view,
    viewport,
    mount,
    writes,
    state,
    rootTop: () => rootTop,
    editor,
    container,
    measure,
    moveCursor: (lineNumber: number) => {
      state.cursorLine = lineNumber;
      cursorChanged({ position: { lineNumber, column: 1 }, source: "keyboard" });
    },
    scrollTo: (top: number) => {
      owner.setScrollTop(top);
    },
  };
}

describe("review viewport geometry ownership", () => {
  it("keeps an unresolved editor inside its reserved section height", () => {
    const current = fixture();
    current.scrollTo(0);
    current.state.containerHeight = () => 150;
    current.viewport.layout();
    expect(current.editor.getLayoutInfo().height).toBe(150);
    current.state.containerHeight = () => 900;
    current.viewport.layout();
    expect(current.editor.getLayoutInfo().height).toBe(562);
  });

  it("keeps a stable render window while crossing file boundaries", () => {
    const current = fixture();
    current.state.containerOffset = 238;
    current.viewport.layout();
    current.scrollTo(0);
    expect(current.editor.getLayoutInfo().height).toBe(562);
    current.editor.layout.mockClear();
    current.scrollTo(200);
    expect(current.editor.getLayoutInfo().height).toBe(562);
    expect(current.editor.layout).not.toHaveBeenCalled();
    current.state.containerOffset = 700;
    current.viewport.layout();
    current.scrollTo(0);
    expect(current.editor.getLayoutInfo().height).toBe(562);
    expect(current.editor.layout).not.toHaveBeenCalled();
  });

  it("repositions a virtual section without rereading geometry", () => {
    const current = fixture();
    current.scrollTo(10_000);
    current.measure.mockClear();
    current.editor.layout.mockClear();
    current.state.sectionTop = 250.25;
    current.viewport.position();
    expect(current.view.getCurrentScrollTop()).toBe(9750);
    expect(Number.parseFloat(current.mount.style.top)).toBe(9750);
    expect(current.measure).not.toHaveBeenCalled();
    expect(current.editor.layout).not.toHaveBeenCalled();
  });

  it("synchronizes nested geometry changes once without measuring writes", () => {
    const current = fixture();
    current.scrollTo(0);
    current.editor.layout.mockClear();
    current.measure.mockClear();
    current.viewport.update(() => {
      current.viewport.setContentHeight(300);
      current.viewport.update(() => current.viewport.setContentHeight(200));
      expect(current.editor.layout).not.toHaveBeenCalled();
    });
    expect(current.editor.getLayoutInfo().height).toBe(200);
    expect(current.editor.layout).toHaveBeenCalledTimes(1);
    expect(current.measure).not.toHaveBeenCalled();
  });

  it("executes diff cleanup without geometry work after disposal", () => {
    const current = fixture();
    current.viewport.dispose();
    current.measure.mockClear();
    current.editor.layout.mockClear();
    const cleanup = vi.fn();
    current.viewport.update(cleanup);
    current.viewport.layout();
    current.viewport.position();
    expect(cleanup).toHaveBeenCalledOnce();
    expect(current.measure).not.toHaveBeenCalled();
    expect(current.editor.layout).not.toHaveBeenCalled();
  });

  it("keeps capture geometry readable while its virtual row is being removed", () => {
    const current = fixture();
    const before = current.viewport.bounds();
    current.state.sectionRemoved = true;
    expect(current.viewport.bounds()).toEqual(before);
    current.viewport.dispose();
    current.viewport.position();
    expect(current.viewport.bounds()).toEqual(before);
  });

  it("reveals a cursor clipped by the outer viewport without resizing Monaco", () => {
    const current = fixture();
    current.scrollTo(0);
    current.state.sectionTop = 450;
    current.viewport.position();
    current.viewport.setContentHeight(200);
    current.editor.layout.mockClear();
    current.measure.mockClear();
    current.moveCursor(8);
    expect(current.rootTop()).toBe(70);
    expect(current.editor.layout).not.toHaveBeenCalled();
    expect(current.measure).not.toHaveBeenCalled();
  });

  it("leaves cursor restoration inside a geometry transaction to its owner", () => {
    const current = fixture();
    current.scrollTo(0);
    current.state.sectionTop = 450;
    current.viewport.position();
    current.viewport.update(() => current.moveCursor(80));
    expect(current.rootTop()).toBe(0);
  });

  it("keeps the unchanged focused cursor visible when a Find view zone is removed", () => {
    const current = fixture();
    current.state.zoneHeight = 27;
    current.viewport.setContentHeight(227);
    current.scrollTo(33);
    current.state.zoneHeight = 0;
    current.viewport.setContentHeight(200);
    expect(current.rootTop()).toBe(6);
  });

  it("does not reveal content-size changes in unfocused editors or diff transactions", () => {
    const current = fixture();
    current.scrollTo(300);
    current.state.widgetFocus = false;
    current.viewport.setContentHeight(800);
    expect(current.rootTop()).toBe(300);
    current.state.widgetFocus = true;
    current.viewport.update(() => current.viewport.setContentHeight(600));
    expect(current.rootTop()).toBe(300);
  });

  it("does not bring an offscreen focused caret back after a content-size change", () => {
    const current = fixture();
    current.scrollTo(300);
    current.viewport.setContentHeight(800);
    expect(current.rootTop()).toBe(300);
  });

  it("does not resize or repaint editors when measured dimensions are unchanged", () => {
    const current = fixture();
    current.editor.layout.mockClear();
    current.editor.render.mockClear();
    current.viewport.layout();
    current.viewport.layout();
    expect(current.editor.layout).not.toHaveBeenCalled();
    expect(current.editor.render).not.toHaveBeenCalled();
  });

  it("keeps content height stable when visible lines no longer need a horizontal scrollbar", () => {
    const current = fixture();
    const before = current.rootTop();
    const height = current.view.getContentHeight();
    current.view.setMaxLineWidth(184);
    expect(current.view.getContentHeight()).toBe(height);
    expect(current.rootTop()).toBe(before);
    expect(current.writes).toEqual([]);
  });

  it("updates scroll geometry synchronously without measuring the DOM", () => {
    const current = fixture();
    current.scrollTo(10_000);
    expect(current.editor.getLayoutInfo().height).toBe(562);
    current.editor.layout.mockClear();
    current.measure.mockClear();
    for (const top of [10_100, 9_900, 30_000]) {
      current.scrollTo(top);
      expect(current.view.getCurrentScrollTop()).toBe(top);
      expect(Number.parseFloat(current.mount.style.top)).toBe(top);
    }
    expect(current.editor.layout).not.toHaveBeenCalled();
    expect(current.measure).not.toHaveBeenCalled();
  });

  it("routes native editor navigation back through the shared scroll owner", () => {
    const current = fixture();
    current.scrollTo(10_000);
    current.measure.mockClear();
    current.view.getScrollable().setScrollPositionNow({ scrollTop: 9_900 });
    expect(current.rootTop()).toBe(9_900);
    expect(Number.parseFloat(current.mount.style.top)).toBe(9_900);
    expect(current.measure).not.toHaveBeenCalled();
  });

  it("preserves fractional section offsets across native scroll roundtrips", () => {
    const current = fixture();
    current.state.containerOffset = 38.25;
    current.viewport.layout();
    current.scrollTo(10_000.25);
    current.view.getScrollable().setScrollPositionNow({ scrollTop: 10_022 });
    expect(current.rootTop()).toBe(10_022.25);
    current.view.getScrollable().setScrollPositionNow({ scrollTop: 10_000 });
    expect(current.rootTop()).toBe(10_000.25);
  });

  it("keeps background editor scroll restoration subordinate to the review owner", () => {
    const current = fixture();
    current.state.widgetFocus = false;
    current.scrollTo(10_000);
    current.writes.length = 0;
    current.measure.mockClear();
    current.view.getScrollable().setScrollPositionNow({ scrollTop: 10_587 });
    expect(current.rootTop()).toBe(10_000);
    expect(current.editor.getScrollTop()).toBe(10_000);
    expect(Number.parseFloat(current.mount.style.top)).toBe(10_000);
    expect(current.writes).toEqual([]);
    expect(current.measure).not.toHaveBeenCalled();
  });

  it("recomputes the bounded window after a resize", () => {
    const current = fixture();
    current.state.duringLayout = () => {
      current.state.duringLayout = () => {};
      current.view.setMaxLineWidth(184);
    };
    current.container.clientWidth += 1;
    current.viewport.layout();
    expect(current.view.getCurrentScrollTop()).toBe(current.view.getScrollHeight() - 562);
    expect(Number.parseFloat(current.mount.style.top)).toBe(current.view.getCurrentScrollTop());
  });
});
