import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const state = vi.hoisted(() => ({
  fail: "",
  failDispose: "",
  live: new Set<string>(),
  order: [] as string[],
  nodes: new Set<unknown>(),
  model: null as unknown,
  widthLayout: undefined as
    | { _maxLineWidth: number; setMaxLineWidth(width: number): void }
    | undefined,
  originalWidth: undefined as unknown,
  resource(name: string) {
    if (this.fail === name) throw new Error(`failed ${name}`);
    this.live.add(name);
    return {
      dispose: () => {
        this.order.push(name);
        this.live.delete(name);
        if (this.failDispose === name) throw new Error(`cleanup ${name}`);
      },
    };
  },
}));
vi.mock("../editor-context", () => ({ editorContexts: { activate: vi.fn() } }));
vi.mock("../editor-contributions", () => ({
  connectTextEditor: () => ({ ...state.resource("binding"), connection: {} }),
}));
vi.mock("../monaco-setup", () => ({
  monaco: { editor: { EditorOption: { lineHeight: 1 }, ScrollType: { Immediate: 1 } } },
}));
vi.mock("./review-editor-options", () => ({ reviewEditorOptions: () => ({}) }));
vi.mock("./review-context", () => ({ collapseUnchanged: () => ({ gapMarkers: [], hidden: [] }) }));
vi.mock("./review-projection-decorations", () => ({
  captureReviewDecorations: () => [],
  sameReviewDecorations: () => true,
}));
vi.mock("./review-mouse-handoff", () => ({ beginExternalMouseDown: vi.fn() }));
vi.mock("./review-editor-activity", () => ({
  createReviewEditorActivity: () => ({
    ...state.resource("activity"),
    retained: () => false,
  }),
}));
vi.mock("../inline-diff", () => ({
  createReviewDiffPaint: (_editor: unknown, presentation: { painted(): void }) => {
    const value = state.resource("paint");
    presentation.painted();
    return { ...value, composerFocused: () => false, commentsRetained: () => false };
  },
}));
vi.mock("./review-editor-viewport", () => ({
  createReviewEditorViewport: () => {
    const viewport = state.resource("viewport");
    const widthLayout = {
      _maxLineWidth: 0,
      setMaxLineWidth(width: number) {
        this._maxLineWidth = width;
      },
    };
    state.widthLayout = widthLayout;
    state.originalWidth = widthLayout.setMaxLineWidth;
    const editor = {
      ...state.resource("editor"),
      _modelData: { viewModel: { viewLayout: widthLayout } },
      getModel: () => state.model,
      createDecorationsCollection: () => {
        const value = state.resource("gaps");
        return { clear: value.dispose, set: vi.fn() };
      },
      onDidContentSizeChange: () => state.resource("content"),
      onDidChangeCursorPosition: () => state.resource("cursor"),
      onDidChangeModelContent: () => state.resource("model-content"),
      onDidChangeModelOptions: () => state.resource("model-options"),
      onDidChangeConfiguration: () => state.resource("configuration"),
      onDidScrollChange: () => state.resource("horizontal"),
      onDidLayoutChange: () => state.resource("layout"),
      getLayoutInfo: () => ({ width: 760, contentWidth: 700 }),
      getScrollWidth: () => Math.max(700, widthLayout._maxLineWidth),
      getScrollLeft: () => 0,
      setScrollLeft: vi.fn(),
      render: vi.fn(),
    };
    return {
      ...viewport,
      editor,
      layout: vi.fn(),
      shift: vi.fn(),
      update: (change: () => void) => change(),
    };
  },
}));

import { createReviewEditor } from "./review-editor";

function node() {
  const value = {
    dataset: {},
    style: { removeProperty: vi.fn() },
    closest: () => ({ dataset: { index: "0" } }),
    append: vi.fn(),
    contains: () => false,
    remove: () => state.nodes.delete(value),
    removeEventListener: vi.fn(),
  };
  state.nodes.add(value);
  return value;
}

function fixture() {
  const model = {
    dispose: vi.fn(),
    onDidChangeLanguage: () => state.resource("language"),
    onDidChangeTokens: () => state.resource("tokens"),
    onDidChangeDecorations: () => state.resource("decorations"),
  };
  state.model = model;
  const onPainted = vi.fn();
  const options = {
    container: { style: {}, append: vi.fn(), closest: () => ({ dataset: { index: "0" } }) },
    scroller: { element: { append: vi.fn(), focus: vi.fn() } },
    model,
    horizontal: {
      get: () => 0,
      set: vi.fn(),
      subscribe: () => state.resource("horizontal-position").dispose,
      bindRange: () => ({ ...state.resource("horizontal-range"), update: vi.fn() }),
    },
    preparedWidth: { minimumContentWidth: 700, viewportWrapping: true },
    comments: { presenter: {}, bind: () => state.resource("comment-layout") },
    onPainted,
    active: () => false,
  } as unknown as Parameters<typeof createReviewEditor>[0];
  return { options, model, onPainted };
}

describe("review editor construction ownership", () => {
  beforeEach(() => {
    state.fail = state.failDispose = "";
    state.live.clear();
    state.nodes.clear();
    state.order.length = 0;
    state.widthLayout = undefined;
    state.originalWidth = undefined;
    vi.stubGlobal("document", { createElement: node, activeElement: null });
  });
  afterEach(() => vi.unstubAllGlobals());

  it.each([
    "viewport",
    "gaps",
    "horizontal-range",
    "model-content",
    "model-options",
    "configuration",
    "horizontal",
    "language",
    "tokens",
    "decorations",
    "layout",
    "horizontal-position",
    "binding",
    "paint",
    "activity",
    "comment-layout",
    "content",
    "cursor",
  ])("releases completed resources when %s construction fails", async (failure) => {
    const { options, model, onPainted } = fixture();
    state.fail = failure;
    expect(() => createReviewEditor(options)).toThrow(`failed ${failure}`);
    await Promise.resolve();
    expect(state.live).toEqual(new Set());
    expect(state.nodes).toEqual(new Set());
    expect(model.dispose).not.toHaveBeenCalled();
    if (state.widthLayout) {
      expect(state.widthLayout.setMaxLineWidth).toBe(state.originalWidth);
      expect(state.widthLayout._maxLineWidth).toBe(0);
    }
    expect(onPainted).not.toHaveBeenCalled();
  });

  it("stops viewport updates before paint cleanup and releases all resources once even if cleanup throws", async () => {
    const { options, model } = fixture();
    const editor = createReviewEditor(options);
    await Promise.resolve();
    state.failDispose = "paint";
    expect(() => editor.dispose()).toThrow("cleanup paint");
    expect(state.order.indexOf("viewport")).toBeLessThan(state.order.indexOf("paint"));
    expect(state.live).toEqual(new Set());
    expect(state.nodes).toEqual(new Set());
    expect(model.dispose).not.toHaveBeenCalled();
    const count = state.order.length;
    editor.dispose();
    expect(state.order).toHaveLength(count);
  });

  it("preserves construction and cleanup failures together", () => {
    const { options } = fixture();
    state.fail = "activity";
    state.failDispose = "paint";
    let error: unknown;
    try {
      createReviewEditor(options);
    } catch (cause) {
      error = cause;
    }
    expect(error).toBeInstanceOf(AggregateError);
    expect((error as AggregateError).errors.map((entry) => entry.message)).toEqual([
      "failed activity",
      "cleanup paint",
    ]);
    expect(state.live).toEqual(new Set());
    expect(state.nodes).toEqual(new Set());
  });
});
