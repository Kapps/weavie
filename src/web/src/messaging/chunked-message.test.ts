import { zstdCompressSync } from "node:zlib";
import { describe, expect, it } from "vitest";
import { ChunkedMessageReceiver } from "./chunked-message";

const chunk = (id: string, index: number, count: number, data: Uint8Array): string =>
  JSON.stringify({
    $weavieChunk: {
      id,
      index,
      count,
      data: Buffer.from(data).toString("base64"),
    },
  });

const parts = (text: string): [Uint8Array, Uint8Array] => {
  const bytes = zstdCompressSync(Buffer.from(text));
  const split = Math.floor(bytes.length / 2);
  return [bytes.subarray(0, split), bytes.subarray(split)];
};

describe("ChunkedMessageReceiver", () => {
  it("reassembles one logical message before exposing it", () => {
    const receiver = new ChunkedMessageReceiver();

    // Produced by ZstdSharp.Port at level 3, the host's encoder.
    const compressed = Buffer.from("KLUv/WAgAcUAAIhjb21wbGV0ZSDimIMg8J+ntQEAGVA1lw==", "base64");
    expect(receiver.ingest(chunk("1", 0, 2, compressed.subarray(0, 12)))).toBeNull();
    expect(receiver.ingest(chunk("1", 1, 2, compressed.subarray(12)))).toBe(
      "complete ☃ 🧵".repeat(32),
    );
  });

  it("reassembles interleaved logical messages independently", () => {
    const receiver = new ChunkedMessageReceiver();

    const first = parts('{"first":"complete"}');
    const second = parts('{"second":"complete"}');
    expect(receiver.ingest(chunk("1", 0, 2, first[0]))).toBeNull();
    expect(receiver.ingest(chunk("2", 0, 2, second[0]))).toBeNull();
    expect(receiver.ingest(chunk("1", 1, 2, first[1]))).toBe('{"first":"complete"}');
    expect(receiver.ingest(chunk("2", 1, 2, second[1]))).toBe('{"second":"complete"}');
  });

  it("passes an ordinary message between chunks without dropping the partial message", () => {
    const receiver = new ChunkedMessageReceiver();

    const [first, last] = parts('{"large":"complete"}');
    expect(receiver.ingest(chunk("1", 0, 2, first))).toBeNull();
    expect(receiver.ingest('{"branches":["main"]}')).toBe('{"branches":["main"]}');
    expect(receiver.ingest(chunk("1", 1, 2, last))).toBe('{"large":"complete"}');
  });

  it("drops an interrupted partial message when the transport resets", () => {
    const receiver = new ChunkedMessageReceiver();
    expect(receiver.ingest(chunk("old", 0, 2, parts("discard")[0]))).toBeNull();

    receiver.reset();

    expect(receiver.ingest(chunk("new", 0, 1, zstdCompressSync(Buffer.from("replacement"))))).toBe(
      "replacement",
    );
  });

  it("passes ordinary messages through unchanged", () => {
    const receiver = new ChunkedMessageReceiver();
    expect(receiver.ingest('{"scope":"host"}')).toBe('{"scope":"host"}');
  });

  it("rejects invalid compressed data", () => {
    const receiver = new ChunkedMessageReceiver();
    expect(() => receiver.ingest(chunk("1", 0, 1, new Uint8Array([1, 2, 3])))).toThrow();
  });

  it("rejects out-of-order chunks", () => {
    const receiver = new ChunkedMessageReceiver();
    expect(() => receiver.ingest(chunk("1", 1, 2, parts("late")[1]))).toThrow(/out-of-order/);
  });
});
