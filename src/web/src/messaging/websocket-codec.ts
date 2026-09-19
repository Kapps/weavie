import type { Zstd } from "@hpcc-js/wasm-zstd";

let initialized: Promise<void> | undefined;
let codec: Zstd | undefined;
const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });

export function initWebSocketCodec(): Promise<void> {
  initialized ??= import("@hpcc-js/wasm-zstd")
    .then(({ Zstd }) => Zstd.load())
    .then((loaded) => {
      codec = loaded;
    });
  return initialized;
}

function readyCodec(): Zstd {
  if (codec === undefined) throw new Error("The WebSocket codec is not initialized.");
  return codec;
}

export function encodeWebSocketMessage(json: string): Uint8Array<ArrayBuffer> {
  return new Uint8Array(readyCodec().compress(encoder.encode(json), 3));
}

export function decodeWebSocketMessage(bytes: Uint8Array): string {
  return decoder.decode(readyCodec().decompress(bytes));
}
