import type * as monaco from "monaco-editor";
import { describe, expect, it } from "vitest";
import type { ClientSession } from "../bridge";
import { createEditorContexts, type TextEditorConnection } from "./editor-context";

function session(): ClientSession {
  return { signal: new AbortController().signal } as ClientSession;
}

function binding(owner: ClientSession, kind: "file" | "review") {
  const lifetime = new AbortController();
  const model = {} as monaco.editor.ITextModel;
  let displayedModel = model;
  const editor = { getModel: () => displayedModel } as monaco.editor.IStandaloneCodeEditor;
  const connection = {
    session: owner,
    kind,
    editor,
    model,
    signal: lifetime.signal,
  } as TextEditorConnection;
  return {
    connection,
    lifetime,
    rebind: () => {
      displayedModel = {} as monaco.editor.ITextModel;
    },
  };
}

describe("owned editor connections", () => {
  it("selects presentation only within its exact session and never substitutes a hidden file editor", () => {
    const contexts = createEditorContexts();
    const a = session();
    const b = session();
    let kind: "file" | "review" = "file";
    contexts.own(a, () => kind);
    contexts.own(b, () => "file");
    const file = binding(a, "file");
    const review = binding(a, "review");
    const other = binding(b, "file");
    contexts.register(file.connection);
    const disposeReview = contexts.register(review.connection);
    contexts.register(other.connection);
    kind = "review";
    expect(contexts.get(a)).toBe(review.connection);
    expect(contexts.get(b)).toBe(other.connection);
    disposeReview();
    expect(contexts.get(a)).toBeUndefined();
    expect(contexts.get(b)).toBe(other.connection);
  });

  it("retires a captured connection when its shared Monaco widget changes models", () => {
    const contexts = createEditorContexts();
    const original = binding(session(), "file");
    contexts.own(original.connection.session, () => "file");
    contexts.register(original.connection);
    const captured = contexts.get(original.connection.session)!;
    original.rebind();
    expect(contexts.live(captured)).toBe(false);
    expect(contexts.fromEditor(captured.editor)).toBeUndefined();
  });

  it("old cleanup cannot unregister a replacement, and session shutdown invalidates both", () => {
    const lifetime = new AbortController();
    const owner = { signal: lifetime.signal } as ClientSession;
    const contexts = createEditorContexts();
    contexts.own(owner, () => "review");
    const first = binding(owner, "review");
    const removeFirst = contexts.register(first.connection);
    const second = { ...first.connection, signal: new AbortController().signal };
    contexts.register(second);
    contexts.activate(second);
    removeFirst();
    expect(contexts.get(owner)).toBe(second);
    expect(contexts.fromEditor(second.editor)).toBe(second);
    lifetime.abort();
    expect(contexts.get(owner)).toBeUndefined();
  });
});
