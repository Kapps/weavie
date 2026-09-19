import { describe, expect, it } from "vitest";
import { ChunkedMessageReceiver } from "./chunked-message";

const chunk = (id: string, index: number, count: number, text: string): string =>
  JSON.stringify({
    $weavieChunk: {
      id,
      index,
      count,
      data: btoa(text),
    },
  });

describe("ChunkedMessageReceiver", () => {
  it("reassembles one logical message before exposing it", () => {
    const receiver = new ChunkedMessageReceiver();

    expect(receiver.ingest(chunk("1", 0, 2, '{"value":"'))).toBeNull();
    expect(receiver.ingest(chunk("1", 1, 2, 'complete"}'))).toBe('{"value":"complete"}');
  });

  it("reassembles interleaved logical messages independently", () => {
    const receiver = new ChunkedMessageReceiver();

    expect(receiver.ingest(chunk("1", 0, 2, '{"first":"'))).toBeNull();
    expect(receiver.ingest(chunk("2", 0, 2, '{"second":"'))).toBeNull();
    expect(receiver.ingest(chunk("1", 1, 2, 'complete"}'))).toBe('{"first":"complete"}');
    expect(receiver.ingest(chunk("2", 1, 2, 'complete"}'))).toBe('{"second":"complete"}');
  });

  it("passes an ordinary message between chunks without dropping the partial message", () => {
    const receiver = new ChunkedMessageReceiver();

    expect(receiver.ingest(chunk("1", 0, 2, '{"large":"'))).toBeNull();
    expect(receiver.ingest('{"branches":["main"]}')).toBe('{"branches":["main"]}');
    expect(receiver.ingest(chunk("1", 1, 2, 'complete"}'))).toBe('{"large":"complete"}');
  });

  it("drops an interrupted partial message when the transport resets", () => {
    const receiver = new ChunkedMessageReceiver();
    expect(receiver.ingest(chunk("old", 0, 2, "discard"))).toBeNull();

    receiver.reset();

    expect(receiver.ingest(chunk("new", 0, 1, "replacement"))).toBe("replacement");
  });

  it("passes ordinary messages through unchanged", () => {
    const receiver = new ChunkedMessageReceiver();
    expect(receiver.ingest('{"scope":"host"}')).toBe('{"scope":"host"}');
  });

  it("rejects out-of-order chunks", () => {
    const receiver = new ChunkedMessageReceiver();
    expect(() => receiver.ingest(chunk("1", 1, 2, "late"))).toThrow(/out-of-order/);
  });
});
