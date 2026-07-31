import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";

const posted = vi.hoisted(() => [] as unknown[]);

vi.mock("../notify/notify", () => ({
  notify: vi.fn(),
}));

const { attachImagePaste, sendPastedImagesFromClipboard } = await import("./paste-image");

function pasteEvent(item: DataTransferItem): ClipboardEvent {
  return {
    clipboardData: { items: { 0: item, length: 1 } },
    preventDefault: vi.fn(),
    stopImmediatePropagation: vi.fn(),
  } as unknown as ClipboardEvent;
}

function imageItem(mime: string, bytes: Uint8Array): DataTransferItem {
  return {
    kind: "file",
    type: mime,
    getAsFile: () => ({
      type: mime,
      arrayBuffer: async () =>
        bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength),
    }),
  } as unknown as DataTransferItem;
}

async function flushAsyncWork(): Promise<void> {
  await Promise.resolve();
  await Promise.resolve();
}

function session(backendId: string, slot: string): ClientSession {
  return {
    connection: { id: backendId },
    address: { slot, incarnation: `${slot}-incarnation` },
    feature: (feature: string) => ({
      publish: (name: string, payload: unknown) => {
        posted.push({ backendId, slot, feature, name, payload });
      },
    }),
  } as unknown as ClientSession;
}

describe("sendPastedImagesFromClipboard", () => {
  beforeEach(() => {
    posted.length = 0;
  });

  it("keeps the paste attached to the session that owns the terminal", async () => {
    let paste: ((event: ClipboardEvent) => void) | undefined;
    const container = {
      addEventListener: (_: string, handler: (event: ClipboardEvent) => void) => {
        paste = handler;
      },
      removeEventListener: vi.fn(),
    } as unknown as HTMLElement;
    attachImagePaste(container, session("remote-1", "shared-slot"));

    paste?.(pasteEvent(imageItem("image/png", new Uint8Array([1, 2, 3]))));
    await flushAsyncWork();

    expect(posted).toEqual([
      {
        backendId: "remote-1",
        slot: "shared-slot",
        feature: "terminal.agent",
        name: "pasteImage",
        payload: { mime: "image/png", dataB64: "AQID" },
      },
    ]);
  });

  it("posts pasted agent images to the host", async () => {
    const event = pasteEvent(imageItem("image/png", new Uint8Array([1, 2, 3])));

    expect(sendPastedImagesFromClipboard(event, session("remote-1", "slot-1"))).toBe(true);
    await flushAsyncWork();

    expect(event.preventDefault).toHaveBeenCalledOnce();
    expect(event.stopImmediatePropagation).toHaveBeenCalledOnce();
    expect(posted).toEqual([
      {
        backendId: "remote-1",
        slot: "slot-1",
        feature: "terminal.agent",
        name: "pasteImage",
        payload: { mime: "image/png", dataB64: "AQID" },
      },
    ]);
  });

  it("leaves text-only paste untouched", () => {
    const event = pasteEvent({
      kind: "string",
      type: "text/plain",
      getAsFile: () => null,
    } as unknown as DataTransferItem);

    expect(sendPastedImagesFromClipboard(event, session("remote-1", "slot-1"))).toBe(false);

    expect(event.preventDefault).not.toHaveBeenCalled();
    expect(event.stopImmediatePropagation).not.toHaveBeenCalled();
    expect(posted).toEqual([]);
  });
});
