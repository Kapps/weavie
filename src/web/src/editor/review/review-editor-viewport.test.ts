import { ScrollbarVisibility } from "@codingame/monaco-vscode-api/vscode/vs/base/common/scrollable";
import type { IEditorConfiguration } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/config/editorConfiguration";
import {
  ConfigurationChangedEvent,
  EditorOption,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/config/editorOptions";
import { ViewLayout } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewLayout/viewLayout";
import type { editor as MonacoEditor } from "monaco-editor";
import { describe, expect, it, onTestFinished, vi } from "vitest";
import { createReviewEditorInput } from "./review-editor-input";
import { createReviewEditorViewport } from "./review-editor-viewport";

vi.mock("./review-editor-input", () => ({
  createReviewEditorInput: vi.fn(({ schedule }: { schedule: () => void }) => ({
    revealCursor: vi.fn(),
    isCursorVisible: () => false,
    scrollChanged: schedule,
    dispose: vi.fn(),
  })),
}));

vi.mock("../monaco-setup", () => ({ monaco: { editor: { ScrollType: { Immediate: 1 } } } }));

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
    height: 568,
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
  const state = { duringLayout: () => {}, containerHeight: () => contentHeight };
  const scroller = {
    clientTop: 0,
    clientHeight: 606,
    get scrollTop() {
      return rootTop;
    },
    set scrollTop(top: number) {
      writes.push(top);
      rootTop = Math.max(0, Math.min(top, contentHeight - 568));
    },
    getBoundingClientRect: () => ({ top: 0 }),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  const container = {
    get clientHeight() {
      return state.containerHeight();
    },
    clientWidth: 716,
    getBoundingClientRect: () => ({ top: 38 - rootTop }),
  };
  const mount = {
    style: { top: "", setProperty: vi.fn() },
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  const editor = {
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
  const viewport = createReviewEditorViewport(
    container as HTMLElement,
    mount as unknown as HTMLElement,
    scroller as unknown as HTMLElement,
    { offsetHeight: 32 } as HTMLElement,
    editor as unknown as MonacoEditor.IStandaloneCodeEditor,
  );
  // Monaco publishes its clamped scroll before this DOM height mirror receives content-size changes.
  const content = view.onDidContentSizeChange(() => {
    contentHeight = view.getContentHeight();
    rootTop = Math.min(rootTop, contentHeight - 568);
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
    scrollTo: (top: number) => {
      scroller.scrollTop = top;
      viewport.layout();
    },
  };
}

describe("review viewport geometry ownership", () => {
  it("flushes pending caret rendering for reveals even when the buffered mount does not move", () => {
    const current = fixture();
    current.editor.layout.mockClear();
    current.editor.render.mockClear();
    vi.mocked(createReviewEditorInput).mock.calls.at(-1)![0].renderReveal();
    expect(current.editor.layout).not.toHaveBeenCalled();
    expect(current.editor.render).toHaveBeenCalledOnce();
  });

  it("keeps an unresolved editor inside its reserved section height", () => {
    const current = fixture();
    current.state.containerHeight = () => 150;
    current.viewport.layout();
    expect(current.editor.getLayoutInfo().height).toBe(150);
    current.state.containerHeight = () => 900;
    current.viewport.layout();
    expect(current.editor.getLayoutInfo().height).toBe(900);
  });

  it("does not relayout or repaint unchanged editors on scroll frames", () => {
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

  it("keeps nearby lines painted without moving Monaco during small forward and reverse scrolls", () => {
    const current = fixture();
    current.scrollTo(10_000);
    const top = current.view.getCurrentScrollTop();
    expect(current.editor.getLayoutInfo().height).toBe(568 * 3);
    current.editor.layout.mockClear();
    current.editor.render.mockClear();
    current.scrollTo(10_100);
    current.scrollTo(9_900);
    expect(current.view.getCurrentScrollTop()).toBe(top);
    expect(Number.parseFloat(current.mount.style.top)).toBe(top);
    expect(current.editor.layout).not.toHaveBeenCalled();
    expect(current.editor.render).not.toHaveBeenCalled();
  });

  it("refills the render window before the visible screen reaches its edge", () => {
    const current = fixture();
    current.scrollTo(10_000);
    const top = current.view.getCurrentScrollTop();
    current.scrollTo(10_600);
    expect(current.view.getCurrentScrollTop()).toBeGreaterThan(top);
    expect(current.view.getCurrentScrollTop()).toBe(10_600 - 568);
    expect(current.rootTop()).toBe(10_600);
    expect(Number.parseFloat(current.mount.style.top)).toBe(current.view.getCurrentScrollTop());
  });

  it("restores the owned render window after Monaco requests an internal scroll", () => {
    const current = fixture();
    current.scrollTo(10_000);
    const top = current.view.getCurrentScrollTop();
    current.view.getScrollable().setScrollPositionNow({ scrollTop: top - 100 });
    expect(current.rootTop()).toBe(10_000);
    const frame = vi.mocked(requestAnimationFrame).mock.calls.at(-1)![0];
    frame(0);
    expect(current.view.getCurrentScrollTop()).toBe(top);
    expect(Number.parseFloat(current.mount.style.top)).toBe(top);
  });

  it("recomputes the bounded window after a resize", () => {
    const current = fixture();
    current.state.duringLayout = () => {
      current.state.duringLayout = () => {};
      current.view.setMaxLineWidth(184);
    };
    current.container.clientWidth += 1;
    current.viewport.layout();
    expect(current.view.getCurrentScrollTop()).toBe(current.view.getScrollHeight() - 568 * 3);
    expect(Number.parseFloat(current.mount.style.top)).toBe(current.view.getCurrentScrollTop());
  });
});
