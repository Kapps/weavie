import { beforeEach, describe, expect, it, vi } from "vitest";

const bridge = vi.hoisted(() => ({
  listener: undefined as
    | ((message: Record<string, unknown>, backendId: string) => void)
    | undefined,
  posted: [] as Array<{ backendId: string; message: Record<string, unknown> }>,
}));

vi.mock("../bridge", () => ({
  onSessionMessage: (listener: (message: Record<string, unknown>, backendId: string) => void) => {
    bridge.listener = listener;
    return () => {};
  },
  postToBackend: (backendId: string, message: Record<string, unknown>) => {
    bridge.posted.push({ backendId, message });
  },
}));

const store = await import("./composer-store");

describe("agent composer attachments", () => {
  beforeEach(() => {
    bridge.posted.length = 0;
  });

  it("captures the backend and blocks submission until the remote upload is ready", async () => {
    const event = pasteEvent(new Blob([new Uint8Array([1, 2, 3])], { type: "image/png" }));
    store.setComposerDraft("remote-a", "slot-a", "describe it");

    expect(store.captureAgentImagePaste(event, "remote-a", "slot-a")).toBe(true);
    expect(store.submitAgentTurn("remote-a", "slot-a")).toBe(false);
    await flushAsyncWork();

    const upload = bridge.posted.find(({ message }) => message.type === "agent-attachment-upload");
    expect(upload?.backendId).toBe("remote-a");
    expect(upload?.message.slot).toBe("slot-a");
    const attachmentId = upload?.message.id as string;

    bridge.listener?.(
      {
        type: "agent-attachment-state",
        slot: "slot-a",
        id: attachmentId,
        status: "ready",
        error: "",
      },
      "remote-a",
    );
    expect(store.submitAgentTurn("remote-a", "slot-a")).toBe(true);

    const submission = bridge.posted.find(({ message }) => message.type === "agent-submit");
    expect(submission).toMatchObject({
      backendId: "remote-a",
      message: {
        slot: "slot-a",
        prompt: "describe it",
        attachmentIds: [attachmentId],
      },
    });
  });

  it("clears only the acknowledged session after an accepted submission", () => {
    store.setComposerDraft("remote-b", "slot-b", "keep me");
    store.setComposerDraft("remote-c", "slot-c", "send me");
    expect(store.submitAgentTurn("remote-c", "slot-c")).toBe(true);
    const submission = bridge.posted.find(({ message }) => message.type === "agent-submit");

    bridge.listener?.(
      {
        type: "agent-submission-state",
        slot: "slot-c",
        id: submission?.message.id,
        attachmentIds: [],
        status: "accepted",
        error: "",
      },
      "remote-c",
    );

    expect(store.composerState("remote-c", "slot-c").draft).toBe("");
    expect(store.composerState("remote-b", "slot-b").draft).toBe("keep me");
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
