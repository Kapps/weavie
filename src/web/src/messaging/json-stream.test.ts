import { describe, expect, it } from "vitest";
import { readJsonStream } from "./json-stream";

describe("JSON response stream", () => {
  it("preserves Unicode and JSON across arbitrary byte boundaries", async () => {
    const bytes = new TextEncoder().encode(
      '{"text":"snowman ☃ 😀\\nquote \\""}\n{"complete":true}\n',
    );
    let offset = 0;
    const body = new ReadableStream<Uint8Array>({
      pull(controller) {
        if (offset === bytes.length) controller.close();
        else controller.enqueue(bytes.slice(offset, ++offset));
      },
    });
    const values = [];
    for await (const value of readJsonStream(new Response(body))) values.push(value);
    expect(values).toEqual([{ text: 'snowman ☃ 😀\nquote "' }, { complete: true }]);
  });

  it("does not consume ahead of its reader and cancels on early completion", async () => {
    let reads = 0;
    let cancelled = false;
    const body = new ReadableStream<Uint8Array>(
      {
        pull(controller) {
          reads++;
          controller.enqueue(new TextEncoder().encode('{"value":1}\n'));
        },
        cancel() {
          cancelled = true;
        },
      },
      { highWaterMark: 0 },
    );
    const iterator = readJsonStream(new Response(body));
    await iterator.next();
    expect(reads).toBe(1);
    await iterator.return(undefined);
    expect(cancelled).toBe(true);
    expect(body.locked).toBe(false);
  });

  it("rejects an incomplete final record", async () => {
    const consume = async () => {
      for await (const _value of readJsonStream(new Response('{"text":"unfinished'))) {
        throw new Error("An incomplete record was published.");
      }
    };
    await expect(consume()).rejects.toThrow("incomplete record");
  });
});
