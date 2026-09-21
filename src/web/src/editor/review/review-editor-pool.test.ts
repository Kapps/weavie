import type { editor } from "monaco-editor";
import { afterEach, describe, expect, it, vi } from "vitest";
import { createEmbeddedEditor } from "../monaco-setup";
import { createReviewEditorPool } from "./review-editor-pool";

vi.mock(
  "@codingame/monaco-vscode-api/vscode/vs/editor/contrib/find/browser/findController",
  () => ({
    FindController: { ID: "find" },
  }),
);
vi.mock("../monaco-setup", () => ({
  createEmbeddedEditor: vi.fn(),
  monaco: {
    editor: {
      ScrollType: { Immediate: 1 },
      EditorOptions: { scrollbar: { defaultValue: { horizontalScrollbarSize: 12 } } },
    },
  },
}));

function fixture() {
  const editors: ReturnType<typeof makeEditor>[] = [];
  function makeEditor() {
    const change = vi.fn();
    return {
      updateOptions: vi.fn(),
      setModel: vi.fn(),
      setPosition: vi.fn(),
      setScrollPosition: vi.fn(),
      getContribution: vi.fn(() => ({ getState: () => ({ change }) })),
      dispose: vi.fn(),
      change,
    };
  }
  vi.mocked(createEmbeddedEditor).mockImplementation(() => {
    const instance = makeEditor();
    editors.push(instance);
    return instance as unknown as editor.IStandaloneCodeEditor;
  });
  vi.stubGlobal("document", {
    createElement: () => ({
      style: { removeProperty: vi.fn() },
      remove: vi.fn(),
    }),
  });
  const container = { append: vi.fn() } as unknown as HTMLElement;
  const model = {} as editor.ITextModel;
  const pool = createReviewEditorPool();
  return { pool, container, model, editors };
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

describe("review editor pool", () => {
  it("grows only to simultaneous demand and reuses released shells", () => {
    const { pool, container, model, editors } = fixture();
    const first = pool.acquire(container, model, true);
    const second = pool.acquire(container, model, true);
    expect(first.editor).not.toBe(second.editor);
    first.release();
    first.release();
    const reused = pool.acquire(container, model, true);
    expect(reused.editor).toBe(first.editor);
    const third = pool.acquire(container, model, true);
    expect(third.editor).not.toBe(reused.editor);
    second.release();
    reused.release();
    third.release();
    for (let i = 0; i < 100; i++) pool.acquire(container, model, true).release();
    expect(editors).toHaveLength(3);
    expect(editors.every((instance) => instance.dispose.mock.calls.length === 0)).toBe(true);
    pool.dispose();
    for (const instance of editors) expect(instance.dispose).toHaveBeenCalledOnce();
  });

  it("detaches models and find state and resets scroll, selection and live read-only overrides", () => {
    const { pool, container, model, editors } = fixture();
    const lease = pool.acquire(container, model, false);
    const instance = editors[0]!;
    const overrides = vi.mocked(createEmbeddedEditor).mock.calls[0]![2];
    expect(overrides.readOnly).toBe(true);
    lease.release();
    expect(instance.setModel).toHaveBeenLastCalledWith(null);
    expect(instance.change).toHaveBeenCalledWith(
      {
        isRevealed: false,
        isReplaceRevealed: false,
        searchString: "",
        replaceString: "",
        searchScope: null,
      },
      false,
    );
    expect(lease.mount.remove).toHaveBeenCalledOnce();
    expect(lease.mount.style.removeProperty).toHaveBeenCalledWith("top");
    const nextModel = {} as editor.ITextModel;
    const next = pool.acquire(container, nextModel, true);
    expect(next.editor).toBe(lease.editor);
    expect(overrides.readOnly).toBe(false);
    expect(instance.updateOptions).toHaveBeenLastCalledWith({ readOnly: false });
    expect(instance.setModel).toHaveBeenLastCalledWith(nextModel);
    expect(instance.setPosition).toHaveBeenLastCalledWith({ lineNumber: 1, column: 1 });
    expect(instance.setScrollPosition).toHaveBeenLastCalledWith({ scrollTop: 0, scrollLeft: 0 }, 1);
    pool.dispose();
  });

  it("disposes idle and leased editors once and rejects acquisitions after teardown", () => {
    const { pool, container, model, editors } = fixture();
    pool.acquire(container, model, true).release();
    const active = pool.acquire(container, model, true);
    pool.acquire(container, model, true).release();
    pool.dispose();
    pool.dispose();
    active.release();
    for (const instance of editors) expect(instance.dispose).toHaveBeenCalledOnce();
    expect(() => pool.acquire(container, model, true)).toThrow("disposed review pool");
  });
});
