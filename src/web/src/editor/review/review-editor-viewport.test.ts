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
  const layoutInfo = { contentWidth: 648, height: 568, verticalScrollbarWidth: 14 };
  const values = new Map<EditorOption, unknown>([
    [EditorOption.layoutInfo, layoutInfo],
    [EditorOption.padding, { top: 6, bottom: 6 }],
    [EditorOption.lineHeight, 22],
    [EditorOption.smoothScrolling, false],
    [EditorOption.scrollBeyondLastLine, false],
    [
      EditorOption.scrollbar,
      {
        horizontal: ScrollbarVisibility.Auto,
        horizontalScrollbarSize: 12,
        ignoreHorizontalScrollbarInContentHeight: false,
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
  const state = { duringLayout: () => {} };
  const scroller = {
    clientTop: 0,
    clientHeight: 606,
    get scrollTop() {
      return rootTop;
    },
    set scrollTop(top: number) {
      writes.push(top);
      rootTop = Math.max(0, Math.min(top, contentHeight - layoutInfo.height));
    },
    getBoundingClientRect: () => ({ top: 0 }),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  };
  const container = {
    get clientHeight() {
      return contentHeight;
    },
    clientWidth: 716,
    getBoundingClientRect: () => ({ top: 38 - rootTop }),
  };
  const mount = { style: { top: "" }, addEventListener: vi.fn(), removeEventListener: vi.fn() };
  const editor = {
    layout: ({ height }: { height: number }) => {
      layoutInfo.height = height;
      const changed: boolean[] = [];
      changed[EditorOption.layoutInfo] = true;
      view.onConfigurationChanged(new ConfigurationChangedEvent(changed));
      state.duringLayout();
    },
    getContentHeight: () => view.getContentHeight(),
    getScrollHeight: () => view.getScrollHeight(),
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
    rootTop = Math.min(rootTop, contentHeight - layoutInfo.height);
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
  };
}

describe("review viewport geometry ownership", () => {
  it("does not turn Monaco's horizontal-scrollbar height clamp into an outer reveal", () => {
    const current = fixture();
    const before = current.rootTop();
    current.view.setMaxLineWidth(184);
    expect(current.rootTop()).toBe(before - 12);
    expect(current.writes).toEqual([]);
  });

  it("still reveals genuine editor movement through the outer scroller", () => {
    const current = fixture();
    const destination = current.view.getCurrentScrollTop() - 100;
    current.view.getScrollable().setScrollPositionNow({ scrollTop: destination });
    expect(current.rootTop()).toBe(destination);
    expect(current.writes).toEqual([destination]);
  });

  it("positions a reveal immediately without reentering Monaco layout", () => {
    const current = fixture();
    const layout = vi.fn();
    current.state.duringLayout = layout;
    const destination = current.view.getCurrentScrollTop() - 100;
    current.view.getScrollable().setScrollPositionNow({ scrollTop: destination });
    expect(current.rootTop()).toBe(destination);
    expect(Number.parseFloat(current.mount.style.top)).toBe(destination);
    expect(layout).not.toHaveBeenCalled();
    const frame = vi.mocked(requestAnimationFrame).mock.calls[0]![0];
    frame(0);
    expect(layout).toHaveBeenCalledOnce();
  });

  it("projects the mount using geometry produced by the current editor layout", () => {
    const current = fixture();
    current.state.duringLayout = () => {
      current.state.duringLayout = () => {};
      current.view.setMaxLineWidth(184);
    };
    current.viewport.layout();
    expect(Number.parseFloat(current.mount.style.top)).toBe(current.rootTop());
    expect(current.view.getCurrentScrollTop()).toBe(current.rootTop());
  });
});
