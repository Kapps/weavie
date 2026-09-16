import type { ICodeEditor } from "@codingame/monaco-vscode-api/vscode/vs/editor/browser/editorBrowser";
import type { IViewModel } from "@codingame/monaco-vscode-api/vscode/vs/editor/common/viewModel";
import { afterEach, expect, it, vi } from "vitest";
import { registerReviewEditorCommands } from "./review-editor-commands";

const state = vi.hoisted(() => {
  const native = Object.fromEntries(
    [
      "CursorPageUp",
      "CursorPageUpSelect",
      "CursorPageDown",
      "CursorPageDownSelect",
      "ScrollLineUp",
      "ScrollLineDown",
      "ScrollPageUp",
      "ScrollPageDown",
      "ScrollEditorTop",
      "ScrollEditorBottom",
      "EditorScroll",
    ].map((name) => [
      name,
      { id: name[0]!.toLowerCase() + name.slice(1), runCoreEditorCommand: vi.fn() },
    ]),
  );
  const original = vi.fn();
  const handlers = new Map<string, { handler: (...args: unknown[]) => unknown }>();
  return { native, original, handlers };
});
vi.mock("@codingame/monaco-vscode-api/vscode/vs/editor/browser/coreCommands", () => ({
  CoreNavigationCommands: state.native,
}));
vi.mock("@codingame/monaco-vscode-api/vscode/vs/platform/commands/common/commands", () => ({
  CommandsRegistry: {
    getCommand: (id: string) => ({ id, handler: state.original }),
    registerCommand: (command: { id: string; handler: (...args: unknown[]) => unknown }) => {
      state.handlers.set(command.id, command);
      return { dispose: () => state.handlers.delete(command.id) };
    },
  },
}));
afterEach(() => vi.clearAllMocks());

it("routes native commands to the focused review and restores registration after the last owner", () => {
  const first = {} as ICodeEditor;
  const second = {} as ICodeEditor;
  const plain = {} as ICodeEditor;
  const model = {} as IViewModel;
  let focused = first;
  const accessor = { get: () => ({ getFocusedCodeEditor: () => focused }) };
  const a = registerReviewEditorCommands(first, () => model);
  const b = registerReviewEditorCommands(second, () => model);
  expect(state.handlers.size).toBe(11);
  const page = state.handlers.get("cursorPageDown")!;
  page.handler(accessor, { source: "keyboard" });
  expect(state.native.CursorPageDown!.runCoreEditorCommand).toHaveBeenCalledWith(model, {
    source: "keyboard",
  });
  focused = plain;
  page.handler(accessor, { source: "keyboard" });
  expect(state.original).toHaveBeenCalledWith(accessor, { source: "keyboard" });
  a.dispose();
  expect(state.handlers.size).toBe(11);
  focused = second;
  state.handlers.get("scrollLineDown")!.handler(accessor);
  expect(state.native.ScrollLineDown!.runCoreEditorCommand).toHaveBeenCalledWith(model, {});
  b.dispose();
  expect(state.handlers.size).toBe(0);
});
