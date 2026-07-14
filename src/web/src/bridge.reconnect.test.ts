import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { WebBoundMessage } from "./bridge";

// Regression guard for the remote-reconnect bug: a restarted runner mints a fresh worker port+token, so the
// transport must RE-RESOLVE its URL on every reconnect (not retry the one it opened with, which is now dead).
// The node test env has no DOM, so stub the browser globals the bridge module touches before importing it.

// A fake WebSocket that records the URL it was opened with and lets the test drive open/drop deterministically.
class FakeWebSocket {
  static readonly OPEN = 1;
  static instances: FakeWebSocket[] = [];
  readyState = 0;
  onopen: (() => void) | null = null;
  onmessage: ((event: { data: string }) => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;
  readonly sent: string[] = [];
  closed = false;

  constructor(readonly url: string) {
    FakeWebSocket.instances.push(this);
  }

  send(data: string): void {
    this.sent.push(data);
  }

  close(): void {
    this.closed = true;
  }

  /** Simulate the socket connecting. */
  open(): void {
    this.readyState = FakeWebSocket.OPEN;
    this.onopen?.();
  }

  /** Simulate the link dropping (a runner restart kills the worker). */
  drop(): void {
    this.readyState = 3;
    this.onclose?.();
  }

  /** Simulate one host frame reaching the page. */
  receive(data: Record<string, unknown>): void {
    this.onmessage?.({ data: JSON.stringify(data) });
  }

  /** Advertise whether the connected host emits an ordered ready replay marker. */
  receiveHostInfo(readyReplayProtocol?: number): void {
    const message: Record<string, unknown> = { type: "host-info", buildNumber: "test" };
    if (readyReplayProtocol !== undefined) {
      message.readyReplayProtocol = readyReplayProtocol;
    }
    this.receive(message);
  }

  /** Complete the latest ready replay with its per-connection correlation id. */
  completeReady(): void {
    this.receive(this.readyAck());
  }

  /** The acknowledgement matching this socket's latest ready frame. */
  readyAck(): Record<string, unknown> {
    const ready = [...this.sent]
      .reverse()
      .map((frame) => JSON.parse(frame) as { type?: string; bridgeId?: string })
      .find((frame) => frame.type === "ready");
    return { type: "bridge-ready", bridgeId: ready?.bridgeId ?? "" };
  }
}

// The bridge module's load-time IIFE reads these; a bare object with no __WEAVIE_BRIDGE_WS__ means "no local
// backend", so importing it constructs no transport of its own — the test drives connectBackend explicitly.
vi.stubGlobal("window", { location: { search: "", protocol: "http:", host: "localhost" } });
vi.stubGlobal("WebSocket", FakeWebSocket);

const bridge = await import("./bridge");

describe("WebSocketTransport reconnect", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    FakeWebSocket.instances.length = 0;
  });
  afterEach(() => {
    bridge.disconnectBackend("remote:test");
    vi.useRealTimers();
  });

  it("re-resolves the URL on each reconnect so it follows a restarted runner's fresh worker", async () => {
    const urls = ["ws://host:9/weavie-bridge?token=old", "ws://host:10/weavie-bridge?token=new"];
    let n = 0;
    const resolveUrl = vi.fn(
      (): Promise<string> => Promise.resolve(urls[Math.min(n++, urls.length - 1)] as string),
    );

    bridge.connectBackend("remote:test", "Test", resolveUrl);
    bridge.setActiveBackendId("remote:test");
    await vi.advanceTimersByTimeAsync(0); // flush the initial resolve → open

    expect(resolveUrl).toHaveBeenCalledTimes(1);
    expect(FakeWebSocket.instances).toHaveLength(1);
    expect(FakeWebSocket.instances[0]?.url).toBe(urls[0]);

    FakeWebSocket.instances[0]?.open();
    expect(bridge.activeBackendPhase()).toBe("connecting");
    FakeWebSocket.instances[0]?.receiveHostInfo(1);
    expect(bridge.activeBackendPhase()).toBe("connecting");
    FakeWebSocket.instances[0]?.receive({ type: "bridge-ready", bridgeId: "another-page" });
    expect(bridge.activeBackendPhase()).toBe("connecting");
    const firstReadyAck = FakeWebSocket.instances[0]?.readyAck() ?? {};
    FakeWebSocket.instances[0]?.receive({
      bridgeId: firstReadyAck.bridgeId,
      type: "bridge-ready",
    });
    expect(bridge.activeBackendPhase()).toBe("online");
    FakeWebSocket.instances[0]?.drop(); // link lost → schedule a reconnect (500ms backoff)
    expect(bridge.activeBackendPhase()).toBe("reconnecting");

    await vi.advanceTimersByTimeAsync(600); // fire the reconnect timer + flush its resolve → open

    // The reconnect re-ran the resolver and opened the runner's NEW worker URL, not the stale one it dropped.
    expect(resolveUrl).toHaveBeenCalledTimes(2);
    expect(FakeWebSocket.instances).toHaveLength(2);
    expect(FakeWebSocket.instances[1]?.url).toBe(urls[1]);
    FakeWebSocket.instances[1]?.open();
    expect(bridge.activeBackendPhase()).toBe("reconnecting");
    FakeWebSocket.instances[1]?.receive(firstReadyAck);
    expect(bridge.activeBackendPhase()).toBe("reconnecting");
    FakeWebSocket.instances[1]?.completeReady();
    expect(bridge.activeBackendPhase()).toBe("online");
  });

  it("negotiates readiness independently with legacy and replay-aware workers", async () => {
    const resolveUrl = vi.fn((): Promise<string> => Promise.resolve("ws://host/weavie-bridge"));
    bridge.connectBackend("remote:test", "Test", resolveUrl);
    bridge.setActiveBackendId("remote:test");
    await vi.advanceTimersByTimeAsync(0);

    FakeWebSocket.instances[0]?.open();
    FakeWebSocket.instances[0]?.receiveHostInfo();
    expect(bridge.activeBackendPhase()).toBe("online");

    FakeWebSocket.instances[0]?.drop();
    await vi.advanceTimersByTimeAsync(600);
    FakeWebSocket.instances[1]?.open();
    FakeWebSocket.instances[1]?.receiveHostInfo(1);
    expect(bridge.activeBackendPhase()).toBe("reconnecting");

    FakeWebSocket.instances[1]?.completeReady();
    expect(bridge.activeBackendPhase()).toBe("online");
  });

  it("keeps editor file requests on their owning backend during a backend handoff", async () => {
    const resolveUrl = vi.fn((): Promise<string> => Promise.resolve("ws://host/weavie-bridge"));
    bridge.connectBackend("remote:test", "Test", resolveUrl);
    bridge.setActiveBackendId("remote:test");
    await vi.advanceTimersByTimeAsync(0);

    const socket = FakeWebSocket.instances[0];
    const replies: WebBoundMessage[] = [];
    const offMessage = bridge.onHostMessage((message) => replies.push(message));
    socket?.open();
    socket?.receiveHostInfo();
    socket?.receive({
      type: "set-editor-session",
      sessionId: "remote-session",
      session: { active: null, open: [] },
    });

    bridge.setActiveBackendId("local");
    bridge.postToEditorBackend({ type: "fs-stat", id: "fs1", path: "/remote/file.png" });

    expect(JSON.parse(socket?.sent.at(-1) ?? "{}")).toMatchObject({
      type: "fs-stat",
      id: "fs1",
      path: "/remote/file.png",
    });
    socket?.receive({ type: "fs-stat-result", id: "fs1", ok: true, exists: true, size: 2 });
    expect(replies.at(-1)).toMatchObject({ type: "fs-stat-result", id: "fs1", ok: true });
    offMessage();
  });

  it("delivers non-control frames whose nested payload mentions bridge-ready", async () => {
    const resolveUrl = vi.fn((): Promise<string> => Promise.resolve("ws://host/weavie-bridge"));
    bridge.connectBackend("remote:test", "Test", resolveUrl);
    bridge.setActiveBackendId("remote:test");
    await vi.advanceTimersByTimeAsync(0);

    const messages: WebBoundMessage[] = [];
    const offMessage = bridge.onHostMessage((message) => messages.push(message));
    FakeWebSocket.instances[0]?.open();
    FakeWebSocket.instances[0]?.receive({
      type: "notify",
      level: "info",
      message: "ordinary payload",
      nested: { type: "bridge-ready" },
    });

    expect(messages.at(-1)).toMatchObject({ type: "notify", message: "ordinary payload" });
    offMessage();
  });

  it("does not re-arm a reconnect when the backend is disposed mid-handshake", async () => {
    let reject: (reason: unknown) => void = () => {};
    const resolveUrl = vi.fn(
      (): Promise<string> =>
        new Promise((_, rej) => {
          reject = rej;
        }),
    );

    bridge.connectBackend("remote:test", "Test", resolveUrl);
    expect(resolveUrl).toHaveBeenCalledTimes(1); // handshake in flight, not yet settled

    bridge.disconnectBackend("remote:test"); // the user removes the agent before it resolves
    reject(new Error("runner unreachable")); // the in-flight handshake then fails
    await vi.advanceTimersByTimeAsync(20_000); // well past any backoff window

    // No socket opened, and — the point — no reconnect loop left running for a backend that's gone.
    expect(FakeWebSocket.instances).toHaveLength(0);
    expect(resolveUrl).toHaveBeenCalledTimes(1);
  });

  it("correlates a ready queued before a socket that fails to open", async () => {
    const resolveUrl = vi.fn((): Promise<string> => Promise.resolve("ws://host/weavie-bridge"));
    bridge.connectBackend("remote:test", "Test", resolveUrl);
    bridge.setActiveBackendId("remote:test");
    await vi.advanceTimersByTimeAsync(0);

    bridge.postToBackend("remote:test", { type: "ready" });
    FakeWebSocket.instances[0]?.drop();
    await vi.advanceTimersByTimeAsync(600);
    FakeWebSocket.instances[1]?.open();
    expect(bridge.activeBackendPhase()).toBe("reconnecting");

    FakeWebSocket.instances[1]?.completeReady();
    expect(bridge.activeBackendPhase()).toBe("online");
  });
});
