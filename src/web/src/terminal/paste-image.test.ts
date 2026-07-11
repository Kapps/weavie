import { beforeEach, describe, expect, it, vi } from "vitest";

const posted = vi.hoisted(() => [] as unknown[]);
vi.mock("../bridge", () => ({
  postToBackend: (backendId: string, message: unknown) => {
    posted.push({ backendId, message });
  },
}));

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

describe("sendPastedImagesFromClipboard", () => {
  beforeEach(() => {
    posted.length = 0;
  });

  it("resolves the backend when the paste occurs after a backend switch", async () => {
    let paste: ((event: ClipboardEvent) => void) | undefined;
    const container = {
      addEventListener: (_: string, handler: (event: ClipboardEvent) => void) => {
        paste = handler;
      },
      removeEventListener: vi.fn(),
    } as unknown as HTMLElement;
    let backendId = "remote-1";
    attachImagePaste(container, () => backendId, "shared-slot");
    backendId = "remote-2";

    paste?.(pasteEvent(imageItem("image/png", new Uint8Array([1, 2, 3]))));
    await flushAsyncWork();

    expect(posted).toEqual([
      {
        backendId: "remote-2",
        message: expect.objectContaining({ type: "term-paste-image", slot: "shared-slot" }),
      },
    ]);
  });

  it("posts pasted agent images to the host", async () => {
    const event = pasteEvent(imageItem("image/png", new Uint8Array([1, 2, 3])));

    expect(sendPastedImagesFromClipboard(event, "remote-1", "slot-1")).toBe(true);
    await flushAsyncWork();

    expect(event.preventDefault).toHaveBeenCalledOnce();
    expect(event.stopImmediatePropagation).toHaveBeenCalledOnce();
    expect(posted).toEqual([
      {
        backendId: "remote-1",
        message: {
          type: "term-paste-image",
          slot: "slot-1",
          session: "claude",
          mime: "image/png",
          dataB64: "AQID",
        },
      },
    ]);
  });

  it("leaves text-only paste untouched", () => {
    const event = pasteEvent({
      kind: "string",
      type: "text/plain",
      getAsFile: () => null,
    } as unknown as DataTransferItem);

    expect(sendPastedImagesFromClipboard(event, "remote-1", "slot-1")).toBe(false);

    expect(event.preventDefault).not.toHaveBeenCalled();
    expect(event.stopImmediatePropagation).not.toHaveBeenCalled();
    expect(posted).toEqual([]);
  });
});
