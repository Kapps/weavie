import type { Page } from "@playwright/test";
import { type MessageEnvelope, parseEnvelope } from "../../src/messaging/message-envelope";
import { decodeTestWebSocketMessage } from "./websocket-codec";

export interface HeldResponse {
  hold(matches: (request: MessageEnvelope) => boolean): void;
  received(): MessageEnvelope | undefined;
  release(): Promise<void>;
}

export async function holdHostResponse(page: Page): Promise<HeldResponse> {
  const socketsKey = "weavie.e2e.held-response-sockets";
  await page.addInitScript((key) => {
    const sockets = new Set<WebSocket>();
    Reflect.set(window, Symbol.for(key), sockets);
    const addListener = EventTarget.prototype.addEventListener;
    EventTarget.prototype.addEventListener = function (...args) {
      if (args[0] === "message" && this instanceof WebSocket) sockets.add(this);
      addListener.apply(this, args);
    };
  }, socketsKey);
  let pending:
    | {
        matches: (request: MessageEnvelope) => boolean;
        requestId: string | null;
        response: MessageEnvelope | undefined;
        send: (() => Promise<void>) | undefined;
      }
    | undefined;
  await page.routeWebSocket("**/*", (socket) => {
    const server = socket.connectToServer();
    socket.onMessage((data) => {
      const message = parseEnvelope(decodeTestWebSocketMessage(data));
      if (
        pending !== undefined &&
        pending.requestId === null &&
        message?.kind === "request" &&
        pending.matches(message)
      )
        pending.requestId = message.requestId;
      server.send(data);
    });
    server.onMessage((data) => {
      const message = parseEnvelope(decodeTestWebSocketMessage(data));
      if (
        pending !== undefined &&
        pending.requestId !== null &&
        message?.kind === "response" &&
        message.requestId === pending.requestId
      ) {
        pending.response = message;
        pending.send = async () => {
          if (typeof data === "string") throw new Error("Expected a binary host response");
          const observed = await page.evaluateHandle(
            ({ key, url, bytes }) => {
              const sockets = Reflect.get(window, Symbol.for(key)) as Set<WebSocket>;
              const source = [...sockets].find(
                (candidate) => candidate.url === url && candidate.readyState === WebSocket.OPEN,
              );
              if (source?.onmessage === null || source === undefined)
                throw new Error("The application has no connected bridge receiver");
              const complete = Promise.withResolvers<void>();
              const receive = (event: MessageEvent): void => {
                if (!(event.data instanceof ArrayBuffer)) return;
                const actual = new Uint8Array(event.data);
                if (actual.length !== bytes.length || !actual.every((byte, i) => byte === bytes[i]))
                  return;
                source.removeEventListener("message", receive);
                queueMicrotask(() => complete.resolve());
              };
              // Registered after the app's receiver: acknowledge the real payload after dispatch.
              source.addEventListener("message", receive);
              return {
                done: complete.promise,
                dispose: () => source.removeEventListener("message", receive),
              };
            },
            { key: socketsKey, url: socket.url(), bytes: [...data] },
          );
          try {
            socket.send(data);
            await observed.evaluate(({ done }) => done);
          } finally {
            await observed.evaluate(({ dispose }) => dispose());
            await observed.dispose();
          }
        };
      } else socket.send(data);
    });
  });
  return {
    hold: (matches) => {
      if (pending !== undefined) throw new Error("A host response is already held");
      pending = { matches, requestId: null, response: undefined, send: undefined };
    },
    received: () => pending?.response,
    release: async () => {
      if (pending?.send === undefined) throw new Error("The held response has not arrived");
      const send = pending.send;
      pending = undefined;
      await send();
    },
  };
}
