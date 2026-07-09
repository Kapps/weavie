import { beforeEach, describe, expect, it, vi } from "vitest";

const posted = vi.hoisted(() => [] as unknown[]);
vi.mock("../bridge", () => ({
  postToHost: (m: unknown) => {
    posted.push(m);
  },
}));

vi.mock("../notify/notify", () => ({
  notify: vi.fn(),
}));

const { sendPastedImagesFromClipboard } = await import("./paste-image");

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

  it("posts pasted agent images to the host", async () => {
    const event = pasteEvent(imageItem("image/png", new Uint8Array([1, 2, 3])));

    expect(sendPastedImagesFromClipboard(event, "slot-1")).toBe(true);
    await flushAsyncWork();

    expect(event.preventDefault).toHaveBeenCalledOnce();
    expect(event.stopImmediatePropagation).toHaveBeenCalledOnce();
    expect(posted).toEqual([
      {
        type: "term-paste-image",
        slot: "slot-1",
        session: "claude",
        mime: "image/png",
        dataB64: "AQID",
      },
    ]);
  });

  it("leaves text-only paste untouched", () => {
    const event = pasteEvent({
      kind: "string",
      type: "text/plain",
      getAsFile: () => null,
    } as unknown as DataTransferItem);

    expect(sendPastedImagesFromClipboard(event, "slot-1")).toBe(false);

    expect(event.preventDefault).not.toHaveBeenCalled();
    expect(event.stopImmediatePropagation).not.toHaveBeenCalled();
    expect(posted).toEqual([]);
  });
});
