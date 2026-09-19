import { zstdCompressSync, zstdDecompressSync } from "node:zlib";
import { beforeAll, describe, expect, it } from "vitest";
import {
  decodeWebSocketMessage,
  encodeWebSocketMessage,
  initWebSocketCodec,
} from "./websocket-codec";

beforeAll(() => initWebSocketCodec());

describe("WebSocket zstd codec", () => {
  it("encodes browser messages as interoperable zstd frames", () => {
    const json = JSON.stringify({ content: "☃ 🧵 source code\n".repeat(10_000) });
    const encoded = encodeWebSocketMessage(json);
    expect(zstdDecompressSync(encoded).toString("utf8")).toBe(json);
    expect(encoded.byteLength).toBeLessThan(new TextEncoder().encode(json).byteLength / 10);
  });

  it("decodes host-generated zstd frames", () => {
    // ZstdSharp.Port level 3 output.
    const frame = Buffer.from("KLUv/WAgAcUAAIhjb21wbGV0ZSDimIMg8J+ntQEAGVA1lw==", "base64");
    expect(decodeWebSocketMessage(frame)).toBe("complete ☃ 🧵".repeat(32));
  });

  it("rejects uncompressed input and invalid UTF-8", () => {
    expect(() => decodeWebSocketMessage(new TextEncoder().encode("{}"))).toThrow();
    expect(() => decodeWebSocketMessage(zstdCompressSync(new Uint8Array([0xff])))).toThrow();
  });
});
