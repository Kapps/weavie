import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("solid-js", async () => await vi.importActual("solid-js/dist/solid.js"));

const host = vi.hoisted(() => ({
  installers: [] as Array<(session: unknown) => undefined | (() => void)>,
  commands: new Map<string, (args: unknown, context: { session: unknown }) => unknown>(),
  pane: null as unknown,
}));
vi.mock("../../bridge", () => ({
  registerSessionFeature: (installer: (session: unknown) => undefined | (() => void)) => {
    host.installers.push(installer);
    return () => {};
  },
}));
vi.mock("../../commands/registry", () => ({
  registerCommand: (id: string, handler: (args: unknown, context: { session: unknown }) => void) =>
    host.commands.set(id, handler),
}));
vi.mock("../../agent/pane-store", () => ({ agentPaneModel: () => host.pane }));
vi.mock("../monaco-setup", () => ({
  monaco: {
    Range: class {
      constructor(
        readonly startLineNumber: number,
        readonly startColumn: number,
      ) {}
    },
    editor: { TrackedRangeStickiness: { NeverGrowsWhenTypingAtEdges: 1 } },
  },
}));

import { createSignal } from "solid-js";
import type { AgentTranscriptSnapshot } from "../../agent/AgentPaneTranscriptTypes";
import { emptyTranscript } from "../../agent/AgentTranscriptSnapshot";
import type { ClientSession } from "../../bridge";
import { CommandIds } from "../../commands/types";
import type { monaco } from "../monaco-setup";
import { addInlineReply, dismissInlineReply, inlineReplies } from "./inline-replies-store";

function openSession() {
  const handlers = new Map<string, (payload: unknown) => void>();
  const session = {
    closed: false,
    feature: () => ({
      on: (name: string, handler: (payload: unknown) => void) => {
        handlers.set(name, handler);
        return () => handlers.delete(name);
      },
    }),
  } as unknown as ClientSession;
  const [historyComplete, setHistoryComplete] = createSignal(true);
  const [transcript, setTranscript] = createSignal(emptyTranscript);
  host.pane = { historyComplete, transcript };
  for (const install of host.installers) install(session);
  const prompts = (...ids: string[]): AgentTranscriptSnapshot => ({
    ...emptyTranscript,
    entries: ids.map((id) => ({
      actionMessage: null,
      detailCount: 0,
      details: [],
      id,
      kind: "message",
      label: "You",
      status: null,
      streaming: false,
      summary: null,
      text: id,
      tone: "user",
      submissionId: id,
    })),
  });
  return {
    session,
    setHistoryComplete,
    show: (...ids: string[]) => setTranscript(prompts(...ids)),
    reject: (id: string, error: string) =>
      handlers.get("submissionState")!({ id, status: "rejected", error }),
  };
}

function openModel() {
  const marks = new Set<string>();
  let disposed = false;
  let onDispose = () => {};
  const model = {
    getLineMaxColumn: () => 8,
    deltaDecorations: (old: string[], added: unknown[]) => {
      for (const mark of old) marks.delete(mark);
      return added.map((_, index) => {
        const mark = `mark-${marks.size + index}`;
        marks.add(mark);
        return mark;
      });
    },
    isDisposed: () => disposed,
    onWillDispose: (listener: () => void) => {
      onDispose = listener;
    },
  } as unknown as monaco.editor.ITextModel;
  return {
    model,
    marks,
    dispose: () => {
      onDispose();
      disposed = true;
    },
  };
}

describe("inline replies", () => {
  beforeEach(() => {
    host.pane = null;
  });

  it("keeps a reply under its line until it is dismissed", () => {
    const { session } = openSession();
    const { model, marks } = openModel();
    addInlineReply(session, model, 3, "ask", "why?");
    expect(inlineReplies(session)).toMatchObject([{ id: "ask", question: "why?", error: null }]);
    expect(marks.size).toBe(1);

    dismissInlineReply(session, "ask");
    expect(inlineReplies(session)).toEqual([]);
    expect(marks.size).toBe(0);
  });

  it("carries the reason a prompt was refused", () => {
    const { session, reject } = openSession();
    addInlineReply(session, openModel().model, 1, "ask", "why?");
    reject("ask", "Agent input is paused while Weavie restarts.");
    expect(inlineReplies(session)[0]?.error).toBe("Agent input is paused while Weavie restarts.");
  });

  it("drops a reply whose prompt left the loaded transcript, and keeps one still on its way", () => {
    const { session, show, setHistoryComplete } = openSession();
    const { model } = openModel();
    addInlineReply(session, model, 1, "answered", "why?");
    addInlineReply(session, model, 2, "queued", "and this?");
    show("answered");
    // A reloading transcript is not a cleared one.
    setHistoryComplete(false);
    show();
    expect(inlineReplies(session).map((reply) => reply.id)).toEqual(["answered", "queued"]);

    setHistoryComplete(true);
    expect(inlineReplies(session).map((reply) => reply.id)).toEqual(["queued"]);
  });

  it("ends with its model, and all at once by command", () => {
    const { session } = openSession();
    const closing = openModel();
    const { model, marks } = openModel();
    addInlineReply(session, closing.model, 1, "closed", "why?");
    addInlineReply(session, model, 1, "first", "why?");
    addInlineReply(session, model, 2, "second", "why?");
    closing.dispose();
    expect(inlineReplies(session).map((reply) => reply.id)).toEqual(["first", "second"]);

    host.commands.get(CommandIds.dismissInlineReplies)!(undefined, { session });
    expect(inlineReplies(session)).toEqual([]);
    expect(marks.size).toBe(0);
  });
});
