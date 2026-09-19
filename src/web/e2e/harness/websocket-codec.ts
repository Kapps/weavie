import {
  decodeWebSocketMessage,
  encodeWebSocketMessage,
} from "../../src/messaging/websocket-codec";

export function decodeTestWebSocketMessage(data: string | Buffer): string {
  if (typeof data === "string") throw new Error("Expected a binary zstd WebSocket frame");
  return decodeWebSocketMessage(data);
}

export function encodeTestWebSocketMessage(json: string): Buffer {
  return Buffer.from(encodeWebSocketMessage(json));
}
