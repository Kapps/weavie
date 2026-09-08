import type * as monaco from "monaco-editor";
import { expect, test, vi } from "vitest";
import { activeEditor, observeEditors, registerEditor, trackEditorFocus } from "./editor-instances";

function documentEditor() {
  const focusListeners = new Set<() => void>();
  const disposeListeners = new Set<() => void>();
  const editor = {
    onDidFocusEditorText: (listener: () => void) => {
      focusListeners.add(listener);
      return { dispose: () => focusListeners.delete(listener) };
    },
    onDidDispose: (listener: () => void) => {
      disposeListeners.add(listener);
      return { dispose: () => disposeListeners.delete(listener) };
    },
  } as unknown as monaco.editor.IStandaloneCodeEditor;
  trackEditorFocus(editor);
  return {
    editor,
    focus: () => {
      for (const listener of focusListeners) listener();
    },
    dispose: () => {
      for (const listener of disposeListeners) listener();
    },
  };
}

test("commands retain the focused section until another editor is focused or it is disposed", () => {
  const main = documentEditor();
  const section = documentEditor();
  registerEditor(main.editor);
  main.focus();
  registerEditor(section.editor);
  expect(activeEditor()).toBe(main.editor);
  section.focus();
  expect(activeEditor()).toBe(section.editor);
  main.dispose();
  expect(activeEditor()).toBe(section.editor);
  section.dispose();
  expect(activeEditor()).toBeNull();
});

test("shared features cover existing and future editors and are released exactly once", () => {
  const main = documentEditor();
  registerEditor(main.editor);
  const cleanups = [vi.fn(), vi.fn()];
  const install = vi.fn().mockReturnValueOnce(cleanups[0]).mockReturnValueOnce(cleanups[1]);
  const stop = observeEditors(install);
  const section = documentEditor();
  registerEditor(section.editor);
  expect(install.mock.calls.map(([editor]) => editor)).toEqual([main.editor, section.editor]);
  section.dispose();
  expect(cleanups[1]).toHaveBeenCalledTimes(1);
  expect(cleanups[0]).not.toHaveBeenCalled();
  stop();
  main.dispose();
  expect(cleanups[0]).toHaveBeenCalledTimes(1);
  expect(cleanups[1]).toHaveBeenCalledTimes(1);
});

test("a focused peek editor receives commands without acquiring document features", () => {
  const main = documentEditor();
  registerEditor(main.editor);
  const install = vi.fn(() => () => {});
  const stop = observeEditors(install);
  main.focus();
  const peek = documentEditor();
  peek.focus();
  expect(activeEditor()).toBe(peek.editor);
  expect(install).toHaveBeenCalledTimes(1);
  main.focus();
  expect(activeEditor()).toBe(main.editor);
  peek.dispose();
  main.dispose();
  stop();
});
