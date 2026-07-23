import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Message } from "vscode-jsonrpc";

// Capture what the transport posts (tagged with the backend it addressed), the inbound handler it registers,
// and a controllable per-backend phase — so the bridge module is fully stubbed (the transport's only dependency).
const bridge = vi.hoisted(() => ({
  posted: [] as Array<Record<string, unknown>>,
  handler: undefined as ((msg: Record<string, unknown>) => void) | undefined,
  offlineBackends: new Set<string>(),
}));
vi.mock("../bridge", () => ({
  postToBackend: (backendId: string, m: Record<string, unknown>) =>
    bridge.posted.push({ backendId, ...m }),
  onHostMessage: (h: (msg: Record<string, unknown>) => void) => {
    bridge.handler = h;
    return () => {};
  },
  backendPhase: (id: string) => (bridge.offlineBackends.has(id) ? "reconnecting" : "online"),
}));

const { openLspChannel } = await import("./lsp-bridge-transport");

function deliver(msg: Record<string, unknown>): void {
  bridge.handler?.(msg);
}

beforeEach(() => {
  bridge.posted.length = 0;
  bridge.offlineBackends.clear();
});

describe("openLspChannel", () => {
  it("asks the owning backend to start the server on open", () => {
    openLspChannel("local", "slotA", "typescript", "ch-start", () => {});
    expect(bridge.posted).toContainEqual({
      backendId: "local",
      type: "lsp-start",
      slot: "slotA",
      server: "typescript",
      channel: "ch-start",
    });
  });

  it("writes a JSON-RPC message as an lsp-data carrying the embedded payload", async () => {
    const channel = openLspChannel("local", "slotA", "typescript", "ch-write", () => {});
    const msg = { jsonrpc: "2.0", id: 1, method: "textDocument/completion" } as unknown as Message;
    await channel.writer.write(msg);
    expect(bridge.posted).toContainEqual({
      backendId: "local",
      type: "lsp-data",
      slot: "slotA",
      channel: "ch-write",
      payload: msg,
    });
  });

  // The misroute regression: a client's frames must reach the backend that OWNS its slot, never whichever
  // backend happens to be active — a local slot's traffic landing on a remote host gets ignored there and
  // leaks the local server (its shutdown/stop never arrive home).
  it("routes each channel's frames to its own backend, independent of any other backend's state", async () => {
    const local = openLspChannel("local", "slotA", "csharp", "ch-local", () => {});
    const remote = openLspChannel("remote:r", "slotB", "gopls", "ch-remote", () => {});

    bridge.offlineBackends.add("remote:r");
    const msg = { jsonrpc: "2.0", id: 2, method: "textDocument/hover" } as unknown as Message;
    await local.writer.write(msg);
    await expect(remote.writer.write(msg)).rejects.toThrow();

    expect(bridge.posted).toContainEqual({
      backendId: "local",
      type: "lsp-data",
      slot: "slotA",
      channel: "ch-local",
      payload: msg,
    });
    expect(
      bridge.posted.filter((m) => m.backendId === "remote:r" && m.type === "lsp-data"),
    ).toEqual([]);

    local.dispose();
    remote.dispose();
    expect(bridge.posted).toContainEqual({
      backendId: "local",
      type: "lsp-stop",
      slot: "slotA",
      channel: "ch-local",
    });
    expect(bridge.posted).toContainEqual({
      backendId: "remote:r",
      type: "lsp-stop",
      slot: "slotB",
      channel: "ch-remote",
    });
  });

  it("routes an inbound lsp-data to the matching channel's reader and ignores others", () => {
    const channel = openLspChannel("local", "slotA", "typescript", "ch-read", () => {});
    const received: unknown[] = [];
    channel.reader.listen((m) => received.push(m));

    const payload = { jsonrpc: "2.0", id: 1, result: 7 };
    deliver({ type: "lsp-data", slot: "slotA", channel: "ch-read", payload });
    deliver({ type: "lsp-data", slot: "slotA", channel: "someone-else", payload: { nope: true } });

    expect(received).toEqual([payload]);
  });

  it("fires onExit with the host reason and closes the reader on lsp-exit", () => {
    let exit: { code: number; reason: string | undefined } | undefined;
    const channel = openLspChannel("local", "slotA", "typescript", "ch-exit", (code, reason) => {
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

  it("rejects a write while the owning backend is offline instead of buffering it", async () => {
    const channel = openLspChannel("local", "slotA", "typescript", "ch-offline", () => {});
    bridge.offlineBackends.add("local");
    bridge.posted.length = 0;

    await expect(channel.writer.write({ jsonrpc: "2.0" } as unknown as Message)).rejects.toThrow();
    expect(bridge.posted.some((m) => m.type === "lsp-data")).toBe(false);
  });

  it("stops the server and stops routing after dispose", () => {
    const channel = openLspChannel("local", "slotA", "typescript", "ch-dispose", () => {});
    const received: unknown[] = [];
    channel.reader.listen((m) => received.push(m));

    channel.dispose();
    expect(bridge.posted).toContainEqual({
      backendId: "local",
      type: "lsp-stop",
      slot: "slotA",
      channel: "ch-dispose",
    });

    deliver({ type: "lsp-data", slot: "slotA", channel: "ch-dispose", payload: { late: true } });
    expect(received).toEqual([]);
  });
});
