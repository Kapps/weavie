import type * as monaco from "monaco-editor";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

const runtime = vi.hoisted(() => ({ sessions: new Map<string, ClientSession>() }));
vi.mock("../bridge", () => ({
  clientSessionAt: (_backend: string, address: { slot: string }): ClientSession | undefined =>
    runtime.sessions.get(address.slot),
}));

const { createActiveEditorContext } = await import("./active-editor-context");
const { sessionFileUri } = await import("./session-uri");

function owner(slot: string): { session: ClientSession; publish: ReturnType<typeof vi.fn> } {
  const publish = vi.fn();
  const session = {
    connection: { id: "local" },
    address: { slot, incarnation: "inc-1" },
    feature: () => ({ publish }),
  } as unknown as ClientSession;
  runtime.sessions.set(slot, session);
  return { session, publish };
}

function editorFor(session: ClientSession, path: string) {
  const listeners = new Map<string, Set<() => void>>();
  const subscribe = (event: string) => (listener: () => void) => {
    const handlers = listeners.get(event) ?? new Set();
    handlers.add(listener);
    listeners.set(event, handlers);
    return { dispose: () => handlers.delete(listener) };
  };
  const emit = (event: string): void => {
    for (const listener of listeners.get(event) ?? []) listener();
  };
  let text = "";
  let model = {
    uri: sessionFileUri(session, path),
    getLanguageId: () => "plaintext",
    getValueInRange: () => text,
  } as unknown as monaco.editor.ITextModel | null;
  const editor = {
    getModel: () => model,
    getSelection: () => ({
      startLineNumber: 1,
      startColumn: 1,
      endLineNumber: 1,
      endColumn: text.length + 1,
      isEmpty: () => text.length === 0,
    }),
    onDidFocusEditorText: subscribe("focus"),
    onDidChangeCursorSelection: subscribe("selection"),
    onDidChangeModel: subscribe("model"),
    onDidDispose: subscribe("dispose"),
  } as unknown as monaco.editor.ICodeEditor;
  return {
    editor,
    focus: () => emit("focus"),
    blur: () => emit("blur"),
    dispose: () => emit("dispose"),
    select: (value: string) => {
      text = value;
      emit("selection");
    },
    setModel: (value: monaco.editor.ITextModel | null) => {
      model = value;
      emit("model");
    },
  };
}

beforeEach(() => {
  runtime.sessions.clear();
  vi.useFakeTimers();
});
afterEach(() => vi.useRealTimers());

describe("active editor context", () => {
  it("publishes only the focused section and retains its selection when focus moves to the agent", () => {
    const { session, publish } = owner("primary");
    const context = createActiveEditorContext();
    const first = editorFor(session, "/work/first.txt");
    const second = editorFor(session, "/work/second.txt");
    context.register(first.editor);
    context.register(second.editor);

    first.select("background initialization");
    second.select("background initialization");
    vi.runAllTimers();
    expect(publish).not.toHaveBeenCalled();

    first.focus();
    first.select("outgoing");
    vi.advanceTimersByTime(100);
    second.focus();
    second.select("selected for the agent");
    first.select("background update");
    first.setModel(null);
    second.blur();
    vi.runAllTimers();
    expect(publish).toHaveBeenCalledExactlyOnceWith("activeChanged", {
      path: "/work/second.txt",
      languageId: "plaintext",
      text: "selected for the agent",
      selection: {
        start: { line: 0, character: 0 },
        end: { line: 0, character: 22 },
        isEmpty: false,
      },
    });
  });

  it("restores an unchanged selection on refocus and routes to the model's owning session", () => {
    const firstOwner = owner("first");
    const secondOwner = owner("second");
    const context = createActiveEditorContext();
    const first = editorFor(firstOwner.session, "/work/shared.txt");
    const second = editorFor(secondOwner.session, "/work/shared.txt");
    context.register(first.editor);
    context.register(second.editor);
    first.focus();
    first.select("retained");
    vi.runAllTimers();
    second.focus();
    vi.runAllTimers();
    first.focus();
    vi.runAllTimers();
    expect(firstOwner.publish).toHaveBeenCalledTimes(2);
    expect(firstOwner.publish).toHaveBeenLastCalledWith(
      "activeChanged",
      expect.objectContaining({ text: "retained" }),
    );
    expect(secondOwner.publish).toHaveBeenCalledExactlyOnceWith(
      "activeChanged",
      expect.objectContaining({ text: "", selection: expect.objectContaining({ isEmpty: true }) }),
    );
  });

  it("supports explicit navigation without stealing keyboard focus", () => {
    const { session, publish } = owner("primary");
    const context = createActiveEditorContext();
    const main = editorFor(session, "/work/main.txt");
    context.register(main.editor);
    context.activate(main.editor);
    vi.runAllTimers();
    expect(publish).toHaveBeenCalledWith(
      "activeChanged",
      expect.objectContaining({ path: "/work/main.txt" }),
    );
  });

  it("cancels disposed owners and ignores detached sessions and transient review models", () => {
    const { session, publish } = owner("primary");
    const context = createActiveEditorContext();
    const first = editorFor(session, "/work/first.txt");
    const second = editorFor(session, "/work/second.txt");
    context.register(first.editor);
    context.register(second.editor);
    first.focus();
    first.dispose();
    vi.runAllTimers();
    expect(publish).not.toHaveBeenCalled();

    second.focus();
    runtime.sessions.clear();
    vi.runAllTimers();
    expect(publish).not.toHaveBeenCalled();

    runtime.sessions.set("primary", session);
    second.setModel({
      uri: sessionFileUri(session, "/work/second.txt").with({ scheme: "weavie-review" }),
    } as monaco.editor.ITextModel);
    vi.runAllTimers();
    expect(publish).not.toHaveBeenCalled();
  });
});
