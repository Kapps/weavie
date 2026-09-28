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

vi.mock("../monaco-setup", () => ({ monaco: { editor: { ScrollType: { Immediate: 1 } } } }));

function fixture() {
  vi.stubGlobal("document", { activeElement: null });
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
    focused: true,
    mountFocused: false,
    widgetFocused: false,
    duringLayout: () => {},
    containerHeight: () => contentHeight,
    containerOffset: 38,
    headerHeight: 32,
    viewportHeight: 594,
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
    append: vi.fn((node: { isConnected: boolean }) => {
      node.isConnected = true;
    }),
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
    isConnected: true,
    contains: () => state.mountFocused,
    remove: vi.fn(() => {
      mount.isConnected = false;
    }),
    style: { top: "", setProperty: vi.fn() },
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  const widgetHost = {
    append: vi.fn((node: { isConnected: boolean }) => {
      node.isConnected = true;
    }),
  };
  const widgets = {
    parentElement: widgetHost,
    isConnected: true,
    contains: () => state.widgetFocused,
    remove: vi.fn(() => {
      widgets.isConnected = false;
    }),
  };
  const editor = {
    hasWidgetFocus: () => state.focused,
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
    getDomNode: () => mount,
    setScrollTop: (top: number) => view.getScrollable().setScrollPositionNow({ scrollTop: top }),
    onDidScrollChange: view.onDidScroll,
    render: vi.fn(),
    renderAsync: vi.fn(),
    dispose: vi.fn(),
  };
  const listeners = new Set<(userInitiated: boolean) => void>();
  const owner: ReviewScroll = {
    element: scroller as unknown as HTMLElement,
    viewport: {
      ...scroller,
      get clientHeight() {
        return state.viewportHeight;
      },
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
  const createEditor = vi.fn(() => editor as unknown as MonacoEditor.IStandaloneCodeEditor);
  const viewport = createReviewEditorViewport(
    container as unknown as HTMLElement,
    mount as unknown as HTMLElement,
    widgets as unknown as HTMLElement,
    owner,
    { getBoundingClientRect: () => ({ height: state.headerHeight }) } as HTMLElement,
    createEditor,
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
    widgets,
    widgetHost,
    writes,
    state,
    rootTop: () => rootTop,
    editor,
    createEditor,
    container,
    measure,
    scrollTo: (top: number) => {
      owner.setScrollTop(top);
    },
  };
}

describe("review viewport geometry ownership", () => {
  it("parks exact unfocused nodes without scroll-time layout and reattaches them on re-entry", () => {
    const current = fixture();
    current.state.focused = false;
    current.state.containerOffset = 20_000;
    current.viewport.layout();
    current.scrollTo(0);
    expect(current.mount.isConnected).toBe(false);
    expect(current.widgets.isConnected).toBe(false);
    current.editor.layout.mockClear();
    current.measure.mockClear();
    for (const top of [100, 200.5, 300.75]) current.scrollTo(top);
    expect(current.measure).not.toHaveBeenCalled();
    expect(current.editor.layout).not.toHaveBeenCalled();
    current.container.clientWidth = 720;
    current.viewport.layout();
    expect(current.editor.getLayoutInfo().width).toBe(720);
    expect(current.mount.isConnected).toBe(false);
    current.scrollTo(20_000);
    expect(current.container.append).toHaveBeenCalledExactlyOnceWith(current.mount);
    expect(current.widgetHost.append).toHaveBeenCalledExactlyOnceWith(current.widgets);
    expect(current.editor.renderAsync).toHaveBeenCalledExactlyOnceWith(true);
    expect(current.createEditor).toHaveBeenCalledOnce();
    expect(current.editor.dispose).not.toHaveBeenCalled();
  });

  it.each([
    "focused",
    "mountFocused",
    "widgetFocused",
  ] as const)("retains an offscreen editor with %s input ownership", (owner) => {
    const current = fixture();
    current.state.focused = false;
    current.state[owner] = true;
    current.state.containerOffset = 20_000;
    current.viewport.layout();
    current.scrollTo(0);
    expect(current.mount.remove).not.toHaveBeenCalled();
    expect(current.widgets.remove).not.toHaveBeenCalled();
    current.state[owner] = false;
    current.scrollTo(10);
    expect(current.mount.isConnected).toBe(false);
    expect(current.widgets.isConnected).toBe(false);
  });
  it("supplies the measured width at construction without remeasuring the section", () => {
    const current = fixture();
    expect(current.createEditor).toHaveBeenCalledExactlyOnceWith({ width: 716, height: 0 });
    expect(current.viewport.editor).toBe(current.editor);
    expect(current.measure).toHaveBeenCalledTimes(2);
  });

  it("commits nested geometry changes once using the final dimensions and scroll", () => {
    const current = fixture();
    const initialScroll = current.view.getCurrentScrollTop();
    current.measure.mockClear();
    current.viewport.update(() => {
      current.state.containerHeight = () => 150;
      current.viewport.layout();
      current.viewport.update(() => {
        current.state.containerHeight = () => 900;
        current.scrollTo(100);
        current.viewport.layout();
      });
      expect(current.measure).not.toHaveBeenCalled();
      expect(current.view.getCurrentScrollTop()).toBe(initialScroll);
    });
    expect(current.measure).toHaveBeenCalledTimes(2);
    expect(current.editor.getLayoutInfo().height).toBe(562);
    expect(current.view.getCurrentScrollTop()).toBe(100);
  });

  it("shifts cached section positions without measuring layout or changing the outer scroll", () => {
    const current = fixture();
    current.scrollTo(10_000);
    const before = current.viewport.bounds();
    current.measure.mockClear();
    current.editor.layout.mockClear();
    current.writes.length = 0;
    current.state.containerOffset += 200;
    current.viewport.shift(200);
    expect(current.viewport.bounds()).toEqual({
      top: before.top - 200,
      bottom: before.bottom - 200,
      height: before.height,
    });
    expect(current.view.getCurrentScrollTop()).toBe(9_800);
    expect(current.measure).not.toHaveBeenCalled();
    expect(current.editor.layout).not.toHaveBeenCalled();
    expect(current.writes).toEqual([]);
    current.viewport.reveal(50);
    expect(current.rootTop()).toBe(250);
    current.viewport.layout();
    expect(current.viewport.bounds().top).toBe(50);
  });

  it("restores geometry ownership when a nested mutation throws", () => {
    const current = fixture();
    expect(() =>
      current.viewport.update(() => {
        current.viewport.update(() => {
          throw new Error("mutation failed");
        });
      }),
    ).toThrow("mutation failed");
    current.measure.mockClear();
    current.viewport.layout();
    expect(current.measure).toHaveBeenCalledTimes(2);
    current.scrollTo(100);
    expect(current.view.getCurrentScrollTop()).toBe(100);
  });

  it("runs diff cleanup without committing geometry after viewport disposal", () => {
    const current = fixture();
    current.scrollTo(10_000);
    current.viewport.dispose();
    current.measure.mockClear();
    current.editor.layout.mockClear();
    current.writes.length = 0;
    const cleanup = vi.fn(() => current.view.setMaxLineWidth(184));
    const top = current.mount.style.top;

    current.viewport.update(cleanup);
    current.viewport.layout();
    current.viewport.shift(100);
    current.viewport.reveal(0);

    expect(cleanup).toHaveBeenCalledOnce();
    expect(current.measure).not.toHaveBeenCalled();
    expect(current.editor.layout).not.toHaveBeenCalled();
    expect(current.mount.style.top).toBe(top);
    expect(current.writes).toEqual([]);
  });

  it("does not commit an update that disposes its viewport", () => {
    const current = fixture();
    current.measure.mockClear();
    current.viewport.update(() => current.viewport.dispose());
    expect(current.measure).not.toHaveBeenCalled();
  });

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

  it("sizes a partly visible file to the physical viewport intersection", () => {
    const current = fixture();
    current.state.containerOffset = 238;
    current.viewport.layout();
    current.scrollTo(0);
    expect(current.editor.getLayoutInfo().height).toBe(362);
    current.scrollTo(200);
    expect(current.editor.getLayoutInfo().height).toBe(562);
    current.state.containerOffset = 700;
    current.viewport.layout();
    current.scrollTo(0);
    expect(current.editor.getLayoutInfo().height).toBe(0);
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

  it("keeps the interior viewport size stable across fractional scroll positions", () => {
    const current = fixture();
    current.state.headerHeight = 34.65625;
    current.state.containerOffset = 40.90625;
    current.scrollTo(10_000);
    current.viewport.layout();
    current.editor.layout.mockClear();
    current.measure.mockClear();
    for (const top of [10_001.125, 10_002.5, 10_003.9, 9_999.25]) {
      current.scrollTo(top);
      expect(current.view.getCurrentScrollTop()).toBe(Math.ceil(top - 0.25));
      expect(current.editor.getLayoutInfo().height).toBe(559);
    }
    expect(current.editor.layout).not.toHaveBeenCalled();
    expect(current.measure).not.toHaveBeenCalled();

    current.state.viewportHeight += 1;
    current.viewport.layout();
    expect(current.editor.layout).toHaveBeenCalledExactlyOnceWith(
      { width: 716, height: 560 },
      true,
    );
  });

  it("clips fractional leading and trailing bands to the file extent", () => {
    const current = fixture();
    current.state.headerHeight = 34.65625;
    current.state.containerOffset = 240.90625;
    current.state.containerHeight = () => 1_000;
    current.viewport.layout();
    current.scrollTo(0);
    expect(current.mount.style.top).toBe("0px");
    expect(current.editor.getLayoutInfo().height).toBe(359);
    current.scrollTo(700.375);
    expect(current.mount.style.top).toBe("501px");
    expect(current.editor.getLayoutInfo().height).toBe(499);
    current.scrollTo(1_200.5);
    expect(current.mount.style.top).toBe("1000px");
    expect(current.editor.getLayoutInfo().height).toBe(0);
  });

  it("does not repeat an offscreen layout that Monaco internally clamps", () => {
    const current = fixture();
    current.state.containerOffset = 20_000;
    current.scrollTo(0);
    current.viewport.layout();
    current.editor.getLayoutInfo().height = 5;
    current.editor.layout.mockClear();
    for (const top of [100, 200.5, 300.75]) current.scrollTo(top);
    current.viewport.layout();
    expect(current.editor.layout).not.toHaveBeenCalled();

    current.scrollTo(20_000);
    expect(current.editor.layout).toHaveBeenCalledExactlyOnceWith(
      { width: 716, height: 562 },
      true,
    );
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
