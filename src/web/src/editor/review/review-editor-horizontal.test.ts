import { afterEach, describe, expect, it, vi } from "vitest";
import type { monaco } from "../monaco-setup";
import { createReviewEditorHorizontal } from "./review-editor-horizontal";
import { ReviewHorizontalPositions } from "./review-horizontal-position";

vi.mock("../monaco-setup", () => ({
  monaco: {
    editor: {
      ScrollType: { Immediate: 1 },
      EditorOption: {
        fontInfo: 0,
        wrappingInfo: 1,
        layoutInfo: 2,
        stopRenderingLineAfter: 3,
      },
    },
  },
}));
vi.mock("./review-projection-decorations", () => ({
  captureReviewDecorations: (model: { visual: object }) => model.visual,
  sameReviewDecorations: (before: object, after: object) => before === after,
}));

afterEach(() => vi.unstubAllGlobals());

function fixture(wrapped: boolean) {
  vi.stubGlobal("document", { createElement: () => ({}) });
  const listeners = new Map<string, Set<(event: unknown) => void>>();
  const on = (name: string) => (listener: (event: unknown) => void) => {
    const held = listeners.get(name) ?? new Set();
    listeners.set(name, held);
    held.add(listener);
    return { dispose: () => held.delete(listener) };
  };
  const emit = (name: string, event: unknown): void => {
    for (const listener of listeners.get(name) ?? []) listener(event);
  };
  const state = { width: 500, left: 0, zone: 0 };
  const width = (): number => Math.max(state.width, layout._maxLineWidth, state.zone);
  const clamp = (): void => {
    const previous = state.left;
    state.left = Math.min(state.left, width() - state.width);
    emit("scroll", { scrollWidthChanged: true, scrollLeftChanged: previous !== state.left });
  };
  const layout = {
    _maxLineWidth: 120,
    setMaxLineWidth(value: number) {
      this._maxLineWidth = value;
      clamp();
    },
  };
  const original = layout.setMaxLineWidth;
  const model = {
    visual: {},
    onDidChangeLanguage: on("language"),
    onDidChangeTokens: on("tokens"),
    onDidChangeDecorations: on("decorations"),
  };
  const editor = {
    _modelData: { viewModel: { viewLayout: layout } },
    getModel: () => model,
    getScrollWidth: width,
    getScrollLeft: () => state.left,
    getLayoutInfo: () => ({ width: state.width + 60, contentWidth: state.width }),
    setScrollLeft: (left: number) => {
      state.left = Math.max(0, Math.min(left, width() - state.width));
      emit("scroll", { scrollLeftChanged: true, scrollWidthChanged: false });
    },
    render: () => layout.setMaxLineWidth(120),
    changeViewZones: (change: (zones: object) => void) =>
      change({
        addZone: (zone: { minWidthInPx: number }) => {
          state.zone = zone.minWidthInPx;
          clamp();
          return "width";
        },
        removeZone: () => {
          state.zone = 0;
          clamp();
        },
      }),
    onDidChangeModelContent: on("content"),
    onDidChangeModelOptions: on("options"),
    onDidChangeConfiguration: on("configuration"),
    onDidScrollChange: on("scroll"),
    onDidLayoutChange: on("layout"),
  } as unknown as monaco.editor.IStandaloneCodeEditor;
  const capture = vi.fn();
  const positions = new ReviewHorizontalPositions(capture);
  const position = positions.forPath("/file.ts");
  const passive = position.bindRange();
  passive.update(800);
  position.set(700);
  const binding = createReviewEditorHorizontal(editor, (change) => change(), position, {
    viewportWrapping: wrapped,
    minimumContentWidth: 1300,
  });
  return {
    binding,
    state,
    position,
    positions,
    passive,
    capture,
    editor,
    layout,
    original,
    model,
    emit,
    listeners,
  };
}

describe("live review horizontal ownership", () => {
  it.each([
    true,
    false,
  ])("keeps first-input X and returns it to passive paint (wrapped=%s)", (wrapped) => {
    const f = fixture(wrapped);
    f.binding.restore(() => {});
    expect(f.editor.getScrollLeft()).toBe(700);
    f.binding.restore(() => f.editor.setScrollLeft(0));
    expect(f.editor.getScrollLeft()).toBe(700);
    f.passive.update(750);
    f.editor.setScrollLeft(780);
    expect(f.position.get()).toBe(780);
    f.binding.dispose();
    expect(f.position.get()).toBe(750);
    expect(f.layout.setMaxLineWidth).toBe(f.original);
    expect([...f.listeners.values()].every((listeners) => listeners.size === 0)).toBe(true);
    f.binding.dispose();
  });

  it("clamps restored snapshots against the active width without a restore assertion failure", () => {
    const f = fixture(true);
    f.binding.restore(() => {});
    f.positions.restore({ "/file.ts": 1800 });
    expect(f.position.get()).toBe(800);
    expect(f.editor.getScrollLeft()).toBe(800);
    f.state.width = 1000;
    f.emit("configuration", { hasChanged: (option: number) => option === 1 });
    f.emit("layout", undefined);
    f.positions.restore({ "/file.ts": 700 });
    expect(f.position.get()).toBe(0);
    expect(f.editor.getScrollLeft()).toBe(0);
    f.binding.dispose();
  });

  it.each([
    "content",
    "options",
    "language",
    "tokens",
    "font",
    "wrap",
    "renderLimit",
    "decorations",
  ])("releases prepared width on %s changes", (name) => {
    const f = fixture(true);
    f.binding.restore(() => {});
    if (name === "font" || name === "wrap" || name === "renderLimit")
      f.emit("configuration", {
        hasChanged: (option: number) => option === (name === "font" ? 0 : name === "wrap" ? 1 : 3),
      });
    else {
      if (name === "decorations") f.model.visual = {};
      f.emit(name, undefined);
    }
    expect(f.layout._maxLineWidth).toBe(120);
    expect(f.position.get()).toBe(0);
    f.layout.setMaxLineWidth(200);
    expect(f.layout._maxLineWidth).toBe(200);
    f.binding.dispose();
  });

  it("keeps the reservation across nonvisual tracking and height-only band layout", () => {
    const f = fixture(true);
    f.binding.restore(() => {});
    f.emit("decorations", undefined);
    f.emit("configuration", { hasChanged: () => false });
    f.layout.setMaxLineWidth(0);
    expect(f.layout._maxLineWidth).toBe(1300);
    expect(f.position.get()).toBe(700);
    f.binding.dispose();
  });

  it("preserves the file offset when a fixed projection resizes before its parked view renders", () => {
    const f = fixture(false);
    f.binding.restore(() => {});
    f.state.width = 600;
    // Monaco resets its line-width cache on layout, before an offscreen view can measure again.
    f.layout.setMaxLineWidth(0);
    f.emit("configuration", { hasChanged: () => false });
    f.emit("layout", undefined);
    expect(f.position.get()).toBe(700);
    expect(f.editor.getScrollLeft()).toBe(700);
    f.passive.update(700);
    f.binding.dispose();
    expect(f.position.get()).toBe(700);
  });

  it("clamps to text width rather than retaining the widest prior viewport", () => {
    const f = fixture(false);
    f.binding.restore(() => {});
    f.state.width = 1600;
    f.layout.setMaxLineWidth(0);
    f.emit("configuration", { hasChanged: () => false });
    f.emit("layout", undefined);
    expect(f.position.get()).toBe(0);
    expect(f.editor.getScrollWidth()).toBe(1600);
    f.state.width = 1000;
    f.emit("configuration", { hasChanged: () => false });
    f.emit("layout", undefined);
    f.position.set(500);
    expect(f.position.get()).toBe(300);
    expect(f.editor.getScrollLeft()).toBe(300);
    f.binding.dispose();
  });
});
