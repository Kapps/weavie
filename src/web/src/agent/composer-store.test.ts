import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

const bridge = vi.hoisted(() => ({
  installers: new Set<(session: ClientSession) => undefined | (() => void)>(),
  sessions: new Map<
    string,
    {
      client: ClientSession;
      handlers: Map<string, (payload: Record<string, unknown>) => void>;
    }
  >(),
  nextIncarnation: 0,
  posted: [] as Array<{
    backendId: string;
    slot: string;
    feature: string;
    name: string;
    payload: Record<string, unknown>;
  }>,
}));

const drafts = new Map<string, string>();
vi.stubGlobal("window", {
  sessionStorage: {
    getItem: (key: string) => drafts.get(key) ?? null,
    setItem: (key: string, value: string) => drafts.set(key, value),
    removeItem: (key: string) => drafts.delete(key),
  },
});

vi.mock("../bridge", () => ({
  registerSessionFeature: (installer: (session: ClientSession) => undefined | (() => void)) => {
    bridge.installers.add(installer);
    return () => {};
  },
}));

const store = await import("./composer-store");

function ensureSession(
  backendId: string,
  slot: string,
): {
  client: ClientSession;
  handlers: Map<string, (payload: Record<string, unknown>) => void>;
} {
  const key = `${backendId}\u0000${slot}`;
  const existing = bridge.sessions.get(key);
  if (existing !== undefined) {
    return existing;
  }
  const handlers = new Map<string, (payload: Record<string, unknown>) => void>();
  const client = {
    closed: false,
    connection: { id: backendId },
    address: { slot, incarnation: `${slot}-incarnation-${++bridge.nextIncarnation}` },
    feature: (feature: string) => ({
      on: (name: string, handler: (payload: Record<string, unknown>) => void) => {
        handlers.set(`${feature}.${name}`, handler);
        return () => handlers.delete(`${feature}.${name}`);
      },
      publish: (name: string, payload: Record<string, unknown>) => {
        bridge.posted.push({ backendId, slot, feature, name, payload });
      },
    }),
  } as unknown as ClientSession;
  const session = { client, handlers };
  bridge.sessions.set(key, session);
  for (const install of bridge.installers) install(client);
  return session;
}

function deliver(
  backendId: string,
  slot: string,
  name: string,
  payload: Record<string, unknown>,
): void {
  ensureSession(backendId, slot).handlers.get(`agent.${name}`)?.(payload);
}

const owner = (backendId: string, slot: string): ClientSession =>
  ensureSession(backendId, slot).client;

describe("agent composer attachments", () => {
  beforeEach(() => {
    bridge.posted.length = 0;
    drafts.clear();
  });

  it("captures the backend and blocks submission until the remote upload is ready", async () => {
    const event = pasteEvent(new Blob([new Uint8Array([1, 2, 3])], { type: "image/png" }));
    const session = owner("remote-a", "slot-a");
    store.setComposerDraft(session, "describe it");

    expect(store.captureAgentImagePaste(event, session)).toBe(true);
    expect(store.submitAgentTurn(session, null)).toBe(false);
    await flushAsyncWork();

    const upload = bridge.posted.find(({ name }) => name === "uploadAttachment");
    expect(upload?.backendId).toBe("remote-a");
    expect(upload?.slot).toBe("slot-a");
    const attachmentId = upload?.payload.id as string;

    deliver("remote-a", "slot-a", "attachmentState", {
      id: attachmentId,
      status: "ready",
      error: "",
    });
    expect(store.submitAgentTurn(session, null)).toBe(true);

    const submission = bridge.posted.find(({ name }) => name === "submit");
    expect(submission).toMatchObject({
      backendId: "remote-a",
      slot: "slot-a",
      payload: {
        prompt: "describe it",
        kind: "prompt",
        commandName: "",
        attachmentIds: [attachmentId],
      },
    });
  });

  it("clears only the acknowledged session after an accepted submission", () => {
    const kept = owner("remote-b", "slot-b");
    const sent = owner("remote-c", "slot-c");
    store.setComposerDraft(kept, "keep me");
    store.setComposerDraft(sent, "send me");
    expect(store.submitAgentTurn(sent, null)).toBe(true);
    const submission = bridge.posted.find(({ name }) => name === "submit");

    deliver("remote-c", "slot-c", "submissionState", {
      id: submission?.payload.id,
      attachmentIds: [],
      status: "accepted",
      error: "",
    });

    expect(store.composerState(sent).draft).toBe("");
    expect(store.composerState(kept).draft).toBe("keep me");
    expect([...drafts.values()]).toEqual(["keep me"]);
  });

  it("submits provider commands semantically while leaving staged attachments alone", async () => {
    const session = owner("remote-command", "slot-command");
    const event = pasteEvent(new Blob([new Uint8Array([1])], { type: "image/png" }));
    store.setComposerDraft(session, "/compact");
    expect(store.captureAgentImagePaste(event, session)).toBe(true);
    await flushAsyncWork();

    expect(store.submitAgentTurn(session, "compact")).toBe(true);
    const submission = bridge.posted.find(({ name }) => name === "submit");
    expect(submission?.payload).toMatchObject({
      prompt: "/compact",
      kind: "providerCommand",
      commandName: "compact",
      attachmentIds: [],
    });

    deliver("remote-command", "slot-command", "submissionState", {
      id: submission?.payload.id,
      attachmentIds: [],
      status: "accepted",
      error: "",
    });
    expect(store.composerState(session).attachments).toHaveLength(1);
  });

  it("restores a draft into a new session incarnation for the same backend and slot", () => {
    const session = owner("remote-reload", "slot-reload");
    store.setComposerDraft(session, "long response");

    bridge.sessions.delete("remote-reload\u0000slot-reload");
    const reloaded = owner("remote-reload", "slot-reload");

    expect(store.composerState(reloaded).draft).toBe("long response");
  });

  it("isolates persisted drafts by backend and slot and removes empty drafts", () => {
    const first = owner("remote-isolation", "slot-one");
    const second = owner("remote-isolation", "slot-two");
    const third = owner("other-backend", "slot-one");

    store.setComposerDraft(first, "first");
    store.setComposerDraft(second, "second");
    store.setComposerDraft(third, "third");
    store.setComposerDraft(second, "");

    expect(store.composerState(first).draft).toBe("first");
    expect(store.composerState(second).draft).toBe("");
    expect(store.composerState(third).draft).toBe("third");
    expect([...drafts.values()].sort()).toEqual(["first", "third"]);
  });
});

function pasteEvent(blob: Blob): ClipboardEvent {
  return {
    clipboardData: {
      items: {
        0: { kind: "file", type: blob.type, getAsFile: () => blob },
        length: 1,
      },
    },
    preventDefault: vi.fn(),
    stopImmediatePropagation: vi.fn(),
  } as unknown as ClipboardEvent;
}

async function flushAsyncWork(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
}
