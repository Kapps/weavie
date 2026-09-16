import type { ViewEventHandler } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewEventHandler";
import {
  VerticalRevealType,
  type ViewRevealRangeRequestEvent,
} from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewEvents";
import type { editor as MonacoEditor } from "monaco-editor";
import { describe, expect, it, vi } from "vitest";
import { registerReviewEditorCommands } from "./review-editor-commands";
import { createReviewEditorInput } from "./review-editor-input";

vi.mock("../monaco-setup", () => ({
  monaco: {
    editor: { EditorOption: { lineHeight: 1 } },
  },
}));

vi.mock("./review-editor-commands", () => ({
  registerReviewEditorCommands: vi.fn(() => ({ dispose: vi.fn() })),
}));

function fixture() {
  const state = { focused: true, syncing: false, caretTop: 350 };
  const scroller = { scrollTop: 200 };
  const dispose = vi.fn();
  let handler: ViewEventHandler;
  const model = {
    cursorConfig: { lineHeight: 20 },
    viewLayout: {
      getVerticalOffsetForLineNumber: (line: number) => state.caretTop + (line - 20) * 20,
      getCurrentViewport: () => ({ top: 0, left: 0, width: 500, height: 1200 }),
    },
    addViewEventHandler: (value: ViewEventHandler) => {
      handler = value;
    },
    removeViewEventHandler: vi.fn(),
  };
  const cursorChanged = async () => {
    handler.onRevealRangeRequest({
      range: { startLineNumber: 20, endLineNumber: 20 },
      selections: null,
      verticalType: VerticalRevealType.Simple,
    } as ViewRevealRangeRequestEvent);
    await Promise.resolve();
  };
  const editor = {
    hasWidgetFocus: () => state.focused,
    getPosition: () => ({ lineNumber: 20, column: 1 }),
    getTopForPosition: () => state.caretTop,
    getOption: () => 20,
    getLayoutInfo: () => ({ horizontalScrollbarHeight: 12 }),
    _getViewModel: () => model,
    onDidChangeModel: () => ({ dispose }),
  };
  const schedule = vi.fn();
  const renderReveal = vi.fn();
  const input = createReviewEditorInput({
    editor: editor as unknown as MonacoEditor.IStandaloneCodeEditor,
    container: {
      getBoundingClientRect: () => ({ top: 100 - scroller.scrollTop }),
    } as HTMLElement,
    scroller: scroller as HTMLElement,
    bounds: () => ({ top: 100, height: 400 }),
    isSyncing: () => state.syncing,
    schedule,
    renderReveal,
  });
  return {
    state,
    scroller,
    input,
    editor,
    schedule,
    renderReveal,
    dispose,
    cursorChanged,
    request: (event: ViewRevealRangeRequestEvent) => handler.onRevealRangeRequest(event),
  };
}

describe("review editor visible-page input", () => {
  it("reveals a caret entering rendered buffer below and above the real viewport", async () => {
    const current = fixture();
    await current.cursorChanged();
    expect(current.scroller.scrollTop).toBe(200);
    current.state.caretTop = 650;
    await current.cursorChanged();
    expect(current.scroller.scrollTop).toBe(282);
    current.state.caretTop = 100;
    await current.cursorChanged();
    expect(current.scroller.scrollTop).toBe(100);
    expect(current.schedule).toHaveBeenCalledTimes(3);
  });

  it("does not preserve background cursors or geometry-induced reveal requests", async () => {
    const current = fixture();
    current.state.caretTop = 2_000;
    current.state.focused = false;
    current.input.revealCursor();
    current.state.focused = true;
    current.state.syncing = true;
    await current.cursorChanged();
    expect(current.scroller.scrollTop).toBe(200);
    expect(current.schedule).not.toHaveBeenCalled();
  });

  it("routes scroll-only commands to the outer viewport without revealing the caret", async () => {
    const current = fixture();
    current.input.scrollChanged(220, 200);
    await Promise.resolve();
    expect(current.scroller.scrollTop).toBe(220);
  });

  it("reveals synchronously for history capture and ignores its resulting internal scroll delta", async () => {
    const current = fixture();
    current.state.caretTop = 650;
    const reveal = current.cursorChanged();
    expect(current.scroller.scrollTop).toBe(282);
    expect(current.renderReveal).toHaveBeenCalledOnce();
    current.input.scrollChanged(900, 200);
    await reveal;
    expect(current.scroller.scrollTop).toBe(282);
  });

  it("reveals Find results while text focus belongs to the Find widget", async () => {
    const current = fixture();
    current.state.focused = false;
    current.state.caretTop = 650;
    await current.cursorChanged();
    expect(current.scroller.scrollTop).toBe(282);
  });

  it("aligns oversized single ranges at the top but leaves oversized multi-selections alone", async () => {
    const current = fixture();
    const range = { startLineNumber: 20, endLineNumber: 60 };
    current.request({
      range: null,
      selections: [range],
      verticalType: VerticalRevealType.Center,
    } as ViewRevealRangeRequestEvent);
    current.input.scrollChanged(900, 200);
    await Promise.resolve();
    expect(current.scroller.scrollTop).toBe(200);
    current.request({
      range,
      selections: null,
      verticalType: VerticalRevealType.Center,
    } as ViewRevealRangeRequestEvent);
    await Promise.resolve();
    expect(current.scroller.scrollTop).toBe(350);
  });

  it("centers native reveal requests inside the physical viewport", async () => {
    const current = fixture();
    current.request({
      range: { startLineNumber: 20, endLineNumber: 20 },
      selections: null,
      verticalType: VerticalRevealType.Center,
    } as ViewRevealRangeRequestEvent);
    await Promise.resolve();
    expect(current.scroller.scrollTop).toBe(166);
    expect(current.input.isCursorVisible()).toBe(true);
    current.state.focused = false;
    expect(current.input.isCursorVisible()).toBe(false);
  });

  it("registers native commands using the physical viewport and disposes its subscriptions", () => {
    const current = fixture();
    const registration = vi.mocked(registerReviewEditorCommands).mock.calls.at(-1)!;
    expect(registration[0]).toBe(current.editor);
    const model = registration[1]();
    expect(model.cursorConfig.pageSize).toBe(17);
    expect(model.viewLayout.getCurrentViewport().height).toBe(388);
    expect(model.viewLayout.getCurrentScrollTop()).toBe(200);
    model.viewLayout.setScrollPosition({ scrollTop: 220 }, 1);
    expect(current.scroller.scrollTop).toBe(220);
    current.input.dispose();
    expect(current.dispose).toHaveBeenCalledTimes(1);
  });
});
