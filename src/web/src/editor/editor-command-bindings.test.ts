import type * as monaco from "monaco-editor";
import { beforeEach, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import { CommandIds } from "../commands/types";
import {
  captureConnectionCommand,
  captureEditorCommand,
  createEditorCommands,
} from "./editor-command-bindings";
import { editorContexts, type TextEditorConnection } from "./editor-context";
import type { EditorController } from "./editor-controller";
import { TabOwner } from "./tab-owner";

const env = vi.hoisted(() => ({ selected: null as ClientSession | null }));
vi.mock("../bridge", () => ({ selectedSession: () => env.selected }));
vi.mock("../commands/registry", () => ({ registerCapturedCommand: vi.fn() }));

function connection(session: ClientSession) {
  const model = { uri: { scheme: "test-proposal" } } as monaco.editor.ITextModel;
  let currentModel = model;
  const options = { readOnly: false };
  let selections = [
    { startLineNumber: 8, startColumn: 3, endLineNumber: 8, endColumn: 9 },
  ] as monaco.Selection[];
  const editor = {
    getRawOptions: () => options,
    focus: vi.fn(),
    trigger: vi.fn(),
    getModel: () => currentModel,
    getSelections: () => selections,
    setSelections: vi.fn((value: monaco.Selection[]) => {
      selections = value;
    }),
  };
  const binding = {
    session,
    tab: tab(session),
    model,
    editor,
    signal: new AbortController().signal,
  } as unknown as TextEditorConnection;
  editorContexts.own(
    session,
    () => tab(session),
    () => {},
  );
  editorContexts.register(binding);
  return {
    binding,
    editor,
    options,
    changeModel: () => {
      currentModel = {} as monaco.editor.ITextModel;
    },
  };
}

beforeEach(() => {
  env.selected = { signal: new AbortController().signal } as ClientSession;
});

it("editable proposals retain text mutations while Revise requires a working-copy file", () => {
  const source = connection(env.selected!);
  source.binding.tab.presentation!.capture = () => ({ state: null, text: null });
  const commands = createEditorCommands({} as EditorController, vi.fn());
  const captured = commands.capture(source.binding);
  captured.get(CommandIds.editorPaste)!(undefined, { session: env.selected });
  expect(source.editor.trigger).toHaveBeenCalledWith(
    "weavie-command",
    "editor.action.clipboardPasteAction",
    null,
  );
  expect(() =>
    captured.get(CommandIds.reviseSelection)!(undefined, { session: env.selected }),
  ).toThrow("Revise requires an editable file");
  source.options.readOnly = true;
  expect(() => captured.get(CommandIds.editorPaste)!(undefined, { session: env.selected })).toThrow(
    "read-only",
  );
  expect(source.editor.trigger).toHaveBeenCalledTimes(1);
});

it("explicit menu capture uses its clicked connection without resolving the current editor", () => {
  const clicked = connection(env.selected!);
  const current = connection(env.selected!);
  editorContexts.activate(current.binding);
  const run = captureConnectionCommand(clicked.binding);
  const action = vi.fn();
  run(action);
  expect(action).toHaveBeenCalledWith(
    clicked.binding,
    expect.objectContaining({ startLineNumber: 8 }),
  );
  expect(current.editor.setSelections).not.toHaveBeenCalled();
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

const tabs = new WeakMap<ClientSession, TabOwner>();
function tab(session: ClientSession): TabOwner {
  let owner = tabs.get(session);
  if (owner === undefined) {
    owner = new TabOwner(session, { kind: "review", path: "weavie:review", viewState: null });
    owner.mount({
      text: true,
      capture: () => ({ state: null, text: { path: "file", line: 1 } }),
      restore: async () => {},
      focus: () => {},
      actions: () => undefined,
    });
    tabs.set(session, owner);
  }
  return owner;
}
