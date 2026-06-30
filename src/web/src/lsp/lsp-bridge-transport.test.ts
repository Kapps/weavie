import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Message } from "vscode-jsonrpc";

// Capture what the transport posts to the host, the inbound handler it registers, and a controllable
// offline flag — so the bridge module is fully stubbed (the transport's only dependency).
const bridge = vi.hoisted(() => ({
  posted: [] as Array<Record<string, unknown>>,
  handler: undefined as ((msg: Record<string, unknown>) => void) | undefined,
  offline: false,
}));
vi.mock("../bridge", () => ({
  postToHost: (m: Record<string, unknown>) => bridge.posted.push(m),
  onHostMessage: (h: (msg: Record<string, unknown>) => void) => {
    bridge.handler = h;
    return () => {};
  },
  activeBackendOffline: () => bridge.offline,
}));

const { openLspChannel } = await import("./lsp-bridge-transport");

function deliver(msg: Record<string, unknown>): void {
  bridge.handler?.(msg);
}

beforeEach(() => {
  bridge.posted.length = 0;
  bridge.offline = false;
});

describe("openLspChannel", () => {
  it("asks the host to start the server on open", () => {
    openLspChannel("slotA", "typescript", "ch-start", () => {});
    expect(bridge.posted).toContainEqual({
      type: "lsp-start",
      slot: "slotA",
      server: "typescript",
      channel: "ch-start",
    });
  });

  it("writes a JSON-RPC message as an lsp-data carrying the embedded payload", async () => {
    const channel = openLspChannel("slotA", "typescript", "ch-write", () => {});
    const msg = { jsonrpc: "2.0", id: 1, method: "textDocument/completion" } as unknown as Message;
    await channel.writer.write(msg);
    expect(bridge.posted).toContainEqual({
      type: "lsp-data",
      slot: "slotA",
      channel: "ch-write",
      payload: msg,
    });
  });

  it("routes an inbound lsp-data to the matching channel's reader and ignores others", () => {
    const channel = openLspChannel("slotA", "typescript", "ch-read", () => {});
    const received: unknown[] = [];
    channel.reader.listen((m) => received.push(m));

    const payload = { jsonrpc: "2.0", id: 1, result: 7 };
    deliver({ type: "lsp-data", slot: "slotA", channel: "ch-read", payload });
    deliver({ type: "lsp-data", slot: "slotA", channel: "someone-else", payload: { nope: true } });

    expect(received).toEqual([payload]);
  });

  it("fires onExit with the host reason and closes the reader on lsp-exit", () => {
    let exit: { code: number; reason: string | undefined } | undefined;
    const channel = openLspChannel("slotA", "typescript", "ch-exit", (code, reason) => {
      exit = { code, reason };
    });
    let closed = false;
    channel.reader.onClose(() => {
      closed = true;
    });

    deliver({
      type: "lsp-exit",
      slot: "slotA",
      channel: "ch-exit",
      code: 1,
      reason: "no server on PATH",
    });

    expect(exit).toEqual({ code: 1, reason: "no server on PATH" });
    expect(closed).toBe(true);
  });

  it("rejects a write while the backend is offline instead of buffering it", async () => {
    const channel = openLspChannel("slotA", "typescript", "ch-offline", () => {});
    bridge.offline = true;
    bridge.posted.length = 0;

    await expect(channel.writer.write({ jsonrpc: "2.0" } as unknown as Message)).rejects.toThrow();
    expect(bridge.posted.some((m) => m.type === "lsp-data")).toBe(false);
  });

  it("stops the server and stops routing after dispose", () => {
    const channel = openLspChannel("slotA", "typescript", "ch-dispose", () => {});
    const received: unknown[] = [];
    channel.reader.listen((m) => received.push(m));

    channel.dispose();
    expect(bridge.posted).toContainEqual({
      type: "lsp-stop",
      slot: "slotA",
      channel: "ch-dispose",
    });

    deliver({ type: "lsp-data", slot: "slotA", channel: "ch-dispose", payload: { late: true } });
    expect(received).toEqual([]);
  });
});
