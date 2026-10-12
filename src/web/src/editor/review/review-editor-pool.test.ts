import { beforeEach, expect, it, vi } from "vitest";

const find = vi.hoisted(() => ({ change: vi.fn() }));
vi.mock(
  "@codingame/monaco-vscode-api/vscode/vs/editor/contrib/find/browser/findController",
  () => ({ CommonFindController: { get: () => ({ getState: () => find }) } }),
);
vi.mock("../monaco-setup", () => ({
  monaco: {
    editor: { EditorOptions: { scrollbar: { defaultValue: { horizontalScrollbarSize: 12 } } } },
  },
  createEmbeddedEditor: () => {
    let model: unknown = null;
    return {
      setModel: vi.fn((next: unknown) => {
        model = next;
      }),
      getModel: () => model,
      dispose: vi.fn(),
    };
  },
}));

import { createReviewEditorPool } from "./review-editor-pool";
import type { ReviewScroll } from "./review-scroll";

const element = () => ({ className: "", style: {}, remove: vi.fn() });
const scroller = { element: { append: vi.fn() } } as unknown as ReviewScroll;

beforeEach(() => {
  vi.stubGlobal("document", { createElement: element });
  find.change.mockClear();
});

it("reuses a released editor with its model detached and Find closed", () => {
  const pool = createReviewEditorPool(scroller);
  const first = pool.lease();
  first.editor.setModel({} as never);
  first.release();
  first.release();

  expect(first.signal.aborted).toBe(true);
  expect(first.editor.getModel()).toBeNull();
  expect(first.mount.remove).toHaveBeenCalledOnce();
  expect(find.change).toHaveBeenCalledExactlyOnceWith(
    { isRevealed: false, searchScope: null },
    false,
  );
  const second = pool.lease();
  expect(second.editor).toBe(first.editor);
  expect(second.signal.aborted).toBe(false);
});

it("keeps a leased editor alive past the pool until its section releases it", () => {
  const pool = createReviewEditorPool(scroller);
  const idle = pool.lease();
  const leased = pool.lease();
  idle.release();

  pool.dispose();
  expect(idle.editor.dispose).toHaveBeenCalledOnce();
  expect(leased.editor.dispose).not.toHaveBeenCalled();
  leased.release();
  expect(leased.editor.dispose).toHaveBeenCalledOnce();
});
