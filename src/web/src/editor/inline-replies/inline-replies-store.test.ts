import { beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("solid-js", async () => await vi.importActual("solid-js/dist/solid.js"));

const host = vi.hoisted(() => ({
  installers: [] as Array<(session: unknown) => undefined | (() => void)>,
  commands: new Map<string, (args: unknown, context: { session: unknown }) => unknown>(),
  pane: null as unknown,
  cleared: [] as string[],
  connection: undefined as unknown,
  owned: undefined as string | undefined,
  select: (_session: unknown): void => {},
  held: [] as string[],
}));
vi.mock("@codingame/monaco-vscode-api/services", () => ({
  ITextModelService: {},
  getService: async () => ({
    createModelReference: async (uri: { path: string }) => {
      host.held.push(uri.path);
      return { dispose: () => host.held.splice(host.held.indexOf(uri.path), 1) };
    },
  }),
}));
vi.mock("../../bridge", () => {
  let leave = (): void => {};
  return {
    registerSessionFeature: (installer: (session: unknown) => undefined | (() => void)) => {
      host.installers.push(installer);
      return () => {};
    },
    registerViewFeature: (installer: (session: unknown) => () => void) => {
      host.select = (session) => {
        leave();
        leave = session === null ? () => {} : installer(session);
      };
      return () => {};
    },
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
    anchor: unknown,
    _selection: unknown,
    sent: (id: string) => void,
  ) => ({ setOpen: () => {}, sent, anchor }),
}));
vi.mock("../../notify/notify", () => ({ notify: () => {} }));
vi.mock("../editor-context", () => ({ editorContexts: { get: () => host.connection } }));
vi.mock("../selection-excerpt", () => ({
  anchorPath: (model: { uri: { scheme: string; path: string; toString(): string } }) =>
    model.uri.scheme === "weavie-file" ? model.uri.path : model.uri.toString(),
  selectionExcerpt: () => ({ label: "notes.txt:1:1-1:8", text: "retry()" }),
}));
vi.mock("../session-uri", () => ({
  SESSION_FILE_SCHEME: "weavie-file",
  sessionUriHostPath: (uri: { path: string }) => uri.path,
}));
vi.mock("../monaco-setup", () => {
  class Range {
    constructor(
      readonly startLineNumber: number,
      readonly startColumn: number,
      readonly endLineNumber: number,
      readonly endColumn: number,
    ) {}
    delta(lines: number) {
      return new Range(
        this.startLineNumber + lines,
        this.startColumn,
        this.endLineNumber + lines,
        this.endColumn,
      );
    }
  }
  return {
    monaco: { Range, editor: { TrackedRangeStickiness: { GrowsOnlyWhenTypingBefore: 3 } } },
  };
});
vi.mock("../../agent/agent-command-owner", () => ({ ownedTarget: () => host.owned }));

import { createSignal } from "solid-js";
import type {
  AgentTranscriptEntry,
  AgentTranscriptSnapshot,
} from "../../agent/AgentPaneTranscriptTypes";
import { emptyTranscript } from "../../agent/AgentTranscriptSnapshot";
import type { AgentAnchor, ClientSession } from "../../bridge";
import { evaluateWhen } from "../../commands/context";
import { CommandIds } from "../../commands/types";
import type { monaco } from "../monaco-setup";
import {
  addInlineReply,
  anchorPlace,
  dismissInlineRepliesOn,
  dismissInlineReply,
  inlineFollowUp,
  inlineReplies,
  placeReply,
  setInlineReplyCollapsed,
} from "./inline-replies-store";

const onLine = (line: number) => ({
  startLineNumber: line,
  startColumn: 1,
  endLineNumber: line,
  endColumn: 8,
});

const anchorOn = (line: number, card: string, path = "/notes.txt") => ({
  path,
  conversationId: null,
  blockId: null,
  place: anchorPlace(onLine(line)),
  quote: `line ${line}`,
  card,
});

function openSession() {
  const handlers = new Map<string, (payload: unknown) => void>();
  const published: Array<{ name: string; payload: unknown }> = [];
  const session = {
    closed: false,
    feature: () => ({
      on: (name: string, handler: (payload: unknown) => void) => {
        handlers.set(name, handler);
        return () => handlers.delete(name);
      },
      publish: (name: string, payload: unknown) => published.push({ name, payload }),
    }),
  } as unknown as ClientSession;
  const [historyComplete, setHistoryComplete] = createSignal(true);
  const [transcript, setTranscript] = createSignal(emptyTranscript);
  host.pane = { historyComplete, transcript };
  for (const install of host.installers) install(session);
  // A prompt row: its submission id, and the anchor and card state it was saved with.
  const prompt = (
    id: string,
    anchor: AgentAnchor | undefined,
    replyCard: AgentTranscriptEntry["replyCard"],
  ): AgentTranscriptEntry => ({
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
    ...(anchor === undefined ? {} : { anchor }),
    ...(replyCard === undefined ? {} : { replyCard }),
  });
  const rows = (entries: AgentTranscriptEntry[]): AgentTranscriptSnapshot => ({
    ...emptyTranscript,
    entries,
  });
  return {
    session,
    published,
    setHistoryComplete,
    prompt,
    load: (...entries: AgentTranscriptEntry[]) => setTranscript(rows(entries)),
    // Rows for prompts sent from this page: each carries the anchor its card was sent with.
    show: (...ids: string[]) =>
      setTranscript(
        rows(
          ids.map((id) => {
            const card = inlineReplies(session).find(
              (reply) => reply.id === id || reply.followUps.includes(id),
            );
            return prompt(id, card?.anchor, undefined);
          }),
        ),
      ),
    settle: (id: string, status: "rejected" | "dropped", error: string) =>
      handlers.get("submissionState")!({ id, attachmentIds: [], status, error }),
  };
}

function openModel(scheme = "weavie-file", text = "") {
  const marks = new Set<string>();
  const lines = new Map<string, ReturnType<typeof onLine> & { isEmpty(): boolean }>();
  let content = text.split("\n");
  let disposed = false;
  const listeners = new Map<string, (event: { isFlush: boolean }) => void>();
  const listen = (name: string, listener: (event: { isFlush: boolean }) => void) => {
    listeners.set(name, listener);
    return { dispose: () => listeners.delete(name) };
  };
  const model = {
    uri: { scheme, path: "/notes.txt", toString: () => `${scheme}:/notes.txt` },
    getLineCount: () => content.length,
    getLineContent: (line: number) => content[line - 1] ?? "",
    validateRange: (range: ReturnType<typeof onLine>) => range,
    // Each mark stays on the code it was made for.
    getDecorationRange: (mark: string) => (marks.has(mark) ? lines.get(mark)! : null),
    deltaDecorations: (old: string[], added: { range: ReturnType<typeof onLine> }[]) => {
      for (const mark of old) marks.delete(mark);
      return added.map(({ range }) => {
        const mark = `mark-${lines.size}`;
        marks.add(mark);
        lines.set(mark, { ...range, isEmpty: () => false });
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
    replaceText: (next: string) => {
      content = next.split("\n");
      marks.clear();
      listeners.get("content")?.({ isFlush: true });
    },
    dispose: () => {
      listeners.get("dispose")?.({ isFlush: false });
      disposed = true;
    },
  };
}

const add = (
  session: ClientSession,
  model: monaco.editor.ITextModel,
  line: number,
  id: string,
  path = "/notes.txt",
) => addInlineReply(session, model, onLine(line), anchorOn(line, id, path), "why?");

const ids = (session: ClientSession) => inlineReplies(session).map((reply) => reply.id);

describe("inline replies", () => {
  beforeEach(() => {
    host.pane = null;
    host.cleared.length = 0;
    host.connection = undefined;
    host.owned = undefined;
    host.held.length = 0;
    host.select(null);
  });

  it("holds a file open while its replies are shown, never a model that is not a file, and ends a deleted file's", async () => {
    const { session } = openSession();
    const settled = () => new Promise((resolve) => setTimeout(resolve));
    add(session, openModel("weavie-review").model, 1, "proposed", "weavie-review:/notes.txt");
    add(session, openModel().model, 1, "ask");
    await settled();
    expect(host.held).toEqual(["/notes.txt"]);

    dismissInlineRepliesOn(session, { path: "/other.txt" } as monaco.Uri);
    expect(ids(session)).toEqual(["proposed", "ask"]);
    dismissInlineRepliesOn(session, { path: "/notes.txt" } as monaco.Uri);
    expect(ids(session)).toEqual(["proposed"]);
    await settled();
    expect(host.held).toEqual([]);
  });

  it("keeps a reply under its code until it is dismissed, which the host records", () => {
    const { session, published } = openSession();
    const { model, marks } = openModel();
    add(session, model, 3, "ask");
    expect(inlineReplies(session)).toMatchObject([{ id: "ask", question: "why?", error: null }]);
    expect(placeReply(session, inlineReplies(session)[0]!, model)).toEqual({
      line: 3,
      moved: false,
    });

    dismissInlineReply(session, "ask");
    expect(inlineReplies(session)).toEqual([]);
    expect(marks.size).toBe(0);
    expect(host.cleared).toEqual(["inline-reply:ask"]);
    expect(published).toEqual([
      { name: "setReplyCard", payload: { submissionId: "ask", state: "dismissed" } },
    ]);
  });

  it("carries the reason a prompt was refused, and ends when the agent drops it undelivered", () => {
    const { session, settle, published } = openSession();
    add(session, openModel().model, 1, "ask");
    settle("ask", "rejected", "Agent input is paused while Weavie restarts.");
    expect(inlineReplies(session)[0]?.error).toBe("Agent input is paused while Weavie restarts.");

    add(session, openModel().model, 1, "queued");
    settle("queued", "dropped", "");
    expect(ids(session)).toEqual(["ask"]);
    // A refused card never reached the host, so dismissing it tells the host nothing.
    dismissInlineReply(session, "ask");
    expect(published).toEqual([]);
  });

  it("keeps the follow-ups asked from a card, less any that went nowhere, each sent with the card's anchor", () => {
    const { session, settle } = openSession();
    add(session, openModel().model, 1, "ask");
    const box = inlineFollowUp(session, inlineReplies(session)[0]!) as unknown as {
      sent(id: string): void;
      anchor: AgentAnchor;
    };
    expect(box.anchor.card).toBe("ask");
    box.sent("more");
    box.sent("refused");
    box.sent("lost");
    settle("refused", "rejected", "Agent input is paused while Weavie restarts.");
    settle("lost", "dropped", "");
    expect(inlineReplies(session)).toMatchObject([{ id: "ask", followUps: ["more"], error: null }]);
  });

  it("drops a follow-up that was rewound away, and keeps its card", () => {
    const { session, show } = openSession();
    add(session, openModel().model, 1, "ask");
    const follow = inlineFollowUp(session, inlineReplies(session)[0]!) as unknown as {
      sent(id: string): void;
    };
    follow.sent("more");
    show("ask", "more");
    show("ask");
    expect(inlineReplies(session)).toMatchObject([{ id: "ask", followUps: [] }]);
  });

  it("comes back from the saved transcript with its follow-ups and state, less the dismissed", () => {
    const { session, load, prompt } = openSession();
    load(
      prompt("first", anchorOn(2, "first"), "collapsed"),
      prompt("plain", undefined, undefined),
      prompt("more", anchorOn(2, "first"), undefined),
      prompt("gone", anchorOn(5, "gone"), "dismissed"),
      prompt("walkthrough", { ...anchorOn(1, "walkthrough"), path: null, blockId: "w" }, undefined),
    );
    expect(inlineReplies(session)).toMatchObject([
      { id: "first", question: "first", followUps: ["more"], collapsed: true },
    ]);
  });

  it("shows a fold or dismissal at once, and holds it until the transcript catches up, however rows land", () => {
    const { session, load, prompt } = openSession();
    const saved = (state: AgentTranscriptEntry["replyCard"]) =>
      load(prompt("ask", anchorOn(2, "ask"), state));
    saved(undefined);
    const collapsed = () => inlineReplies(session)[0]?.collapsed;

    // Folded then opened again before either row lands: the first row's arrival must not fold it.
    setInlineReplyCollapsed(session, "ask", true);
    setInlineReplyCollapsed(session, "ask", false);
    saved("collapsed");
    expect(collapsed()).toBe(false);
    saved(undefined);
    expect(collapsed()).toBe(false);

    dismissInlineReply(session, "ask");
    expect(ids(session)).toEqual([]);
    saved(undefined);
    expect(ids(session)).toEqual([]);
    saved("dismissed");
    expect(ids(session)).toEqual([]);
  });

  it("finds its code again in a file that changed, and stays where the code was once it is gone", () => {
    const { session, load, prompt } = openSession();
    load(prompt("ask", anchorOn(2, "ask"), undefined));
    const reply = inlineReplies(session)[0]!;
    const shifted = openModel("weavie-file", "new\nlines\nline 1\nline 2");
    expect(placeReply(session, reply, shifted.model)).toEqual({ line: 4, moved: false });

    shifted.replaceText("nothing\nlike it\nnow");
    expect(placeReply(session, reply, shifted.model)).toEqual({ line: 2, moved: true });
  });

  it("is collapsed and dismissed by command: the one named, else the one in view nearest the cursor", () => {
    const { session, published } = openSession();
    const { model } = openModel();
    add(session, model, 2, "upper");
    add(session, model, 9, "lower");
    add(session, model, 40, "offscreen");
    let cursor = 7;
    host.connection = {
      model,
      editor: {
        getPosition: () => ({ lineNumber: cursor }),
        getVisibleRanges: () => [{ startLineNumber: 1, endLineNumber: 30 }],
      },
    };
    const run = (id: string, args: unknown) => host.commands.get(id)!(args, { session });
    const collapsed = () => inlineReplies(session).map((reply) => reply.collapsed);

    run(CommandIds.toggleInlineReply, undefined);
    expect(collapsed()).toEqual([false, true, false]);
    run(CommandIds.toggleInlineReply, { id: "upper" });
    expect(collapsed()).toEqual([true, true, false]);
    expect(published.map(({ payload }) => payload)).toEqual([
      { submissionId: "lower", state: "collapsed" },
      { submissionId: "upper", state: "collapsed" },
    ]);
    cursor = 38;
    run(CommandIds.dismissInlineReply, undefined);
    expect(ids(session)).toEqual(["upper", "offscreen"]);
    // The card the user is in wins over the cursor.
    host.owned = "offscreen";
    run(CommandIds.dismissInlineReply, undefined);
    expect(ids(session)).toEqual(["upper"]);
    expect(() => run(CommandIds.dismissInlineReply, { id: "gone" })).toThrow("no inline reply");
  });

  it("drops a reply whose prompt left the loaded transcript, and keeps one still on its way", () => {
    const { session, show, setHistoryComplete } = openSession();
    const { model } = openModel();
    add(session, model, 1, "answered");
    add(session, model, 2, "queued");
    show("answered");
    // A reloading transcript is not a cleared one.
    setHistoryComplete(false);
    show();
    expect(ids(session)).toEqual(["answered", "queued"]);

    setHistoryComplete(true);
    expect(ids(session)).toEqual(["queued"]);
  });

  it("outlives its model and the text its mark was in, and ends all at once by command", () => {
    const { session } = openSession();
    host.select(session);
    const closing = openModel();
    const { model, marks, listening } = openModel();
    add(session, closing.model, 1, "closed");
    add(session, model, 1, "first");
    add(session, model, 2, "second");
    closing.dispose();
    expect(closing.listening()).toBe(0);
    expect(ids(session)).toEqual(["closed", "first", "second"]);

    expect(evaluateWhen("agentInlineReplies")).toBe(true);
    host.commands.get(CommandIds.dismissInlineReplies)!(undefined, { session });
    expect(inlineReplies(session)).toEqual([]);
    expect(evaluateWhen("agentInlineReplies")).toBe(false);
    expect(marks.size).toBe(0);
    expect(listening()).toBe(0);
  });
});
