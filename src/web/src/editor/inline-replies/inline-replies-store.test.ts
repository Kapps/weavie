import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("solid-js", async () => await vi.importActual("solid-js/dist/solid.js"));

const host = vi.hoisted(() => ({
  installers: [] as Array<(session: unknown) => undefined | (() => void)>,
  commands: new Map<string, (args: unknown, context: { session: unknown }) => unknown>(),
  pane: null as unknown,
  cleared: [] as string[],
  connection: undefined as unknown,
  select: (_session: unknown): void => {},
}));
vi.mock("../../bridge", async () => {
  const solid = await vi.importActual<typeof import("solid-js")>("solid-js/dist/solid.js");
  const [selected, select] = solid.createSignal<unknown>(null);
  host.select = select;
  return {
    registerSessionFeature: (installer: (session: unknown) => undefined | (() => void)) => {
      host.installers.push(installer);
      return () => {};
    },
    selectedSession: selected,
  };
});
vi.mock("../../commands/registry", () => ({
  registerCommand: (id: string, handler: (args: unknown, context: { session: unknown }) => void) =>
    host.commands.set(id, handler),
}));
vi.mock("../../agent/pane-store", () => ({ agentPaneModel: () => host.pane }));
vi.mock("../../agent/composer-store", () => ({
  clearReplyComposer: (_session: unknown, key: string) => host.cleared.push(key),
  followUpComposer: (
    _session: unknown,
    _key: string,
    _selection: unknown,
    sent: (id: string) => void,
  ) => ({ setOpen: () => {}, sent }),
}));
vi.mock("../../notify/notify", () => ({ notify: () => {} }));
vi.mock("../editor-context", () => ({ editorContexts: { get: () => host.connection } }));
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
import { evaluateWhen } from "../../commands/context";
import { CommandIds } from "../../commands/types";
import type { monaco } from "../monaco-setup";
import {
  addInlineReply,
  dismissInlineReply,
  inlineFollowUp,
  inlineReplies,
} from "./inline-replies-store";

const EXCERPT = { label: "notes.txt:1:1-1:8", text: "retry()" };

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
    settle: (id: string, status: "rejected" | "dropped", error: string) =>
      handlers.get("submissionState")!({ id, attachmentIds: [], status, error }),
  };
}

function openModel() {
  const marks = new Set<string>();
  const lines = new Map<string, number>();
  let disposed = false;
  const listeners = new Map<string, (event: { isFlush: boolean }) => void>();
  const listen = (name: string, listener: (event: { isFlush: boolean }) => void) => {
    listeners.set(name, listener);
    return { dispose: () => listeners.delete(name) };
  };
  const model = {
    getLineMaxColumn: () => 8,
    // Each mark sits on the line it was made for.
    getDecorationRange: (mark: string) => ({ startLineNumber: lines.get(mark)! }),
    deltaDecorations: (old: string[], added: { range: { startLineNumber: number } }[]) => {
      for (const mark of old) marks.delete(mark);
      return added.map(({ range }) => {
        const mark = `mark-${lines.size}`;
        marks.add(mark);
        lines.set(mark, range.startLineNumber);
        return mark;
      });
    },
    isDisposed: () => disposed,
    onWillDispose: (listener: () => void) => listen("dispose", listener),
    onDidChangeContent: (listener: (event: { isFlush: boolean }) => void) =>
      listen("content", listener),
  } as unknown as monaco.editor.ITextModel;
  return {
    model,
    marks,
    listening: () => listeners.size,
    replaceText: () => listeners.get("content")?.({ isFlush: true }),
    dispose: () => {
      listeners.get("dispose")?.({ isFlush: false });
      disposed = true;
    },
  };
}

describe("inline replies", () => {
  beforeEach(() => {
    host.pane = null;
    host.cleared.length = 0;
    host.connection = undefined;
    host.select(null);
  });

  it("keeps a reply under its line until it is dismissed", () => {
    const { session } = openSession();
    const { model, marks } = openModel();
    addInlineReply(session, model, 3, "ask", "why?", EXCERPT);
    expect(inlineReplies(session)).toMatchObject([{ id: "ask", question: "why?", error: null }]);
    expect(marks.size).toBe(1);

    dismissInlineReply(session, "ask");
    expect(inlineReplies(session)).toEqual([]);
    expect(marks.size).toBe(0);
    expect(host.cleared).toEqual(["inline-reply:ask"]);
  });

  it("carries the reason a prompt was refused, and ends when the agent drops it undelivered", () => {
    const { session, settle } = openSession();
    addInlineReply(session, openModel().model, 1, "ask", "why?", EXCERPT);
    settle("ask", "rejected", "Agent input is paused while Weavie restarts.");
    expect(inlineReplies(session)[0]?.error).toBe("Agent input is paused while Weavie restarts.");

    addInlineReply(session, openModel().model, 1, "queued", "and this?", EXCERPT);
    settle("queued", "dropped", "");
    expect(inlineReplies(session).map((reply) => reply.id)).toEqual(["ask"]);
  });

  it("keeps the follow-ups asked from a card, less any that went nowhere", () => {
    const { session, settle } = openSession();
    addInlineReply(session, openModel().model, 1, "ask", "why?", EXCERPT);
    const sent = (id: string) =>
      (
        inlineFollowUp(session, inlineReplies(session)[0]!) as unknown as {
          sent(id: string): void;
        }
      ).sent(id);
    sent("more");
    sent("refused");
    sent("lost");
    settle("refused", "rejected", "Agent input is paused while Weavie restarts.");
    settle("lost", "dropped", "");
    expect(inlineReplies(session)).toMatchObject([{ id: "ask", followUps: ["more"], error: null }]);
  });

  it("is collapsed and dismissed by command, at or nearest above the cursor", () => {
    const { session } = openSession();
    const { model } = openModel();
    addInlineReply(session, model, 2, "upper", "why?", EXCERPT);
    addInlineReply(session, model, 9, "lower", "why?", EXCERPT);
    host.connection = { model, editor: { getPosition: () => ({ lineNumber: 5 }) } };
    const run = (id: string, args: unknown) => host.commands.get(id)!(args, { session });

    run(CommandIds.toggleInlineReply, undefined);
    expect(inlineReplies(session).map((reply) => reply.collapsed)).toEqual([true, false]);
    run(CommandIds.toggleInlineReply, { id: "lower" });
    expect(inlineReplies(session).map((reply) => reply.collapsed)).toEqual([true, true]);
    run(CommandIds.dismissInlineReply, undefined);
    expect(inlineReplies(session).map((reply) => reply.id)).toEqual(["lower"]);
    expect(() => run(CommandIds.dismissInlineReply, { id: "gone" })).toThrow("no inline reply");
  });

  it("drops a reply whose prompt left the loaded transcript, and keeps one still on its way", () => {
    const { session, show, setHistoryComplete } = openSession();
    const { model } = openModel();
    addInlineReply(session, model, 1, "answered", "why?", EXCERPT);
    addInlineReply(session, model, 2, "queued", "and this?", EXCERPT);
    show("answered");
    // A reloading transcript is not a cleared one.
    setHistoryComplete(false);
    show();
    expect(inlineReplies(session).map((reply) => reply.id)).toEqual(["answered", "queued"]);

    setHistoryComplete(true);
    expect(inlineReplies(session).map((reply) => reply.id)).toEqual(["queued"]);
  });

  it("ends with its model or the text its mark was in, and all at once by command", () => {
    const { session } = openSession();
    host.select(session);
    const closing = openModel();
    const replaced = openModel();
    const { model, marks, listening } = openModel();
    addInlineReply(session, closing.model, 1, "closed", "why?", EXCERPT);
    addInlineReply(session, replaced.model, 1, "replaced", "why?", EXCERPT);
    addInlineReply(session, model, 1, "first", "why?", EXCERPT);
    addInlineReply(session, model, 2, "second", "why?", EXCERPT);
    closing.dispose();
    replaced.replaceText();
    expect(inlineReplies(session).map((reply) => reply.id)).toEqual(["first", "second"]);
    expect(replaced.listening()).toBe(0);

    expect(evaluateWhen("agentInlineReplies")).toBe(true);
    host.commands.get(CommandIds.dismissInlineReplies)!(undefined, { session });
    expect(inlineReplies(session)).toEqual([]);
    expect(evaluateWhen("agentInlineReplies")).toBe(false);
    expect(marks.size).toBe(0);
    expect(listening()).toBe(0);
  });
});
