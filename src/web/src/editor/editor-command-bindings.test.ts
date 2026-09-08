import type * as monaco from "monaco-editor";
import { beforeEach, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import { captureEditorCommand } from "./editor-command-bindings";
import { editorContexts, type TextEditorConnection } from "./editor-context";

const env = vi.hoisted(() => ({ selected: null as ClientSession | null }));
vi.mock("../bridge", () => ({ selectedSession: () => env.selected }));
vi.mock("../commands/registry", () => ({ registerCapturedCommand: vi.fn() }));

function connection(session: ClientSession) {
  const model = {} as monaco.editor.ITextModel;
  let currentModel = model;
  let selections = [
    { startLineNumber: 8, startColumn: 3, endLineNumber: 8, endColumn: 9 },
  ] as monaco.Selection[];
  const editor = {
    getModel: () => currentModel,
    getSelections: () => selections,
    setSelections: vi.fn((value: monaco.Selection[]) => {
      selections = value;
    }),
  };
  const binding = {
    session,
    kind: "review",
    model,
    editor,
    signal: new AbortController().signal,
  } as unknown as TextEditorConnection;
  editorContexts.own(session, () => "review");
  editorContexts.register(binding);
  return {
    binding,
    editor,
    changeModel: () => {
      currentModel = {} as monaco.editor.ITextModel;
    },
  };
}

beforeEach(() => {
  env.selected = { signal: new AbortController().signal } as ClientSession;
});

it("keeps a captured review selection when another editor becomes the session's command context", () => {
  const original = connection(env.selected!);
  const run = captureEditorCommand(env.selected);
  const replacement = connection(env.selected!);
  editorContexts.activate(replacement.binding);
  const action = vi.fn();
  run(action);
  expect(action).toHaveBeenCalledWith(
    original.binding,
    expect.objectContaining({ startLineNumber: 8 }),
  );
  expect(replacement.editor.setSelections).not.toHaveBeenCalled();
});

it("declines an incoming command for another session instead of acting on the selected session", () => {
  const original = connection(env.selected!);
  const owner = env.selected;
  env.selected = { signal: new AbortController().signal } as ClientSession;
  const replacement = connection(env.selected);
  const run = captureEditorCommand(owner);
  const action = vi.fn();
  expect(() => run(action)).toThrow("no longer displayed");
  expect(action).not.toHaveBeenCalled();
  expect(original.editor.setSelections).not.toHaveBeenCalled();
  expect(replacement.editor.setSelections).not.toHaveBeenCalled();
});

it("an old captured command cannot use a shared widget after its model binding changes", () => {
  const original = connection(env.selected!);
  const run = captureEditorCommand(env.selected);
  original.changeModel();
  const action = vi.fn();
  expect(() => run(action)).toThrow("no longer displayed");
  expect(action).not.toHaveBeenCalled();
  expect(original.editor.setSelections).not.toHaveBeenCalled();
});
