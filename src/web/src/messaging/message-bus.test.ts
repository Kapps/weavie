import { describe, expect, it, vi } from "vitest";
import { MessageBus } from "./message-bus";
import { parseEnvelope, type SessionAddress } from "./message-envelope";

const address = (slot: string, incarnation: string): SessionAddress => ({ slot, incarnation });

describe("session-owned message bus", () => {
  it("admits only complete, internally consistent envelopes", () => {
    const valid = {
      scope: "session",
      session: address("a", "a1"),
      kind: "event",
      requestId: null,
      feature: "dummy",
      name: "changed",
      payload: {},
      error: null,
    };

    expect(parseEnvelope(JSON.stringify(valid))).toEqual(valid);
    expect(parseEnvelope(JSON.stringify({ ...valid, session: address("", "a1") }))).toBeNull();
    expect(parseEnvelope(JSON.stringify({ ...valid, requestId: "unexpected" }))).toBeNull();
    expect(
      parseEnvelope(
        JSON.stringify({
          ...valid,
          scope: "host",
          session: address("a", "a1"),
        }),
      ),
    ).toBeNull();
    expect(parseEnvelope(JSON.stringify({ ...valid, scope: "host", session: {} }))).toBeNull();
  });

  it("routes a naive feature through its owner while a different session is selected", async () => {
    const a = address("a", "a1");
    const b = address("b", "b1");
    let selected = b;
    let client: MessageBus;
    let host: MessageBus;
    client = new MessageBus("session", a, (json) => {
      const envelope = parseEnvelope(json);
      if (envelope !== null) {
        host.receive(envelope);
      }
    });
    host = new MessageBus("session", a, (json) => {
      const envelope = parseEnvelope(json);
      if (envelope !== null) {
        client.receive(envelope);
      }
    });
    let value = 0;
    host.feature("dummy").handle<{ by: number }, { value: number }>("increment", ({ by }) => {
      value += by;
      return { value };
    });

    const result = await client
      .feature("dummy")
      .request<{ value: number }, { by: number }>("increment", { by: 3 });

    expect(selected).toEqual(b);
    expect(result.value).toBe(3);
    expect(value).toBe(3);
    selected = a;
  });

  it("does not admit an envelope for an earlier incarnation of the same slot", async () => {
    const current = new MessageBus("session", address("main", "new"), () => undefined);
    let calls = 0;
    current.feature("dummy").on("event", () => {
      calls += 1;
    });

    current.receive({
      scope: "session",
      session: address("main", "old"),
      kind: "event",
      requestId: null,
      feature: "dummy",
      name: "event",
      payload: {},
      error: null,
    });
    await Promise.resolve();

    expect(calls).toBe(0);
  });

  it("serializes one feature while different features run in parallel", async () => {
    const bus = new MessageBus("session", address("a", "a1"), () => undefined);
    const releaseFirst = Promise.withResolvers<void>();
    const firstEntered = Promise.withResolvers<void>();
    const secondEntered = Promise.withResolvers<void>();
    const otherEntered = Promise.withResolvers<void>();
    const order: string[] = [];
    const counter = bus.feature("counter");
    counter.on<{ step: string }>("first", async ({ step }) => {
      order.push(`${step}-entered`);
      firstEntered.resolve();
      await releaseFirst.promise;
      order.push(`${step}-finished`);
    });
    counter.on<{ step: string }>("second", ({ step }) => {
      order.push(`${step}-entered`);
      secondEntered.resolve();
    });
    bus.feature("other").on("run", () => otherEntered.resolve());

    bus.receive({
      scope: "session",
      session: address("a", "a1"),
      kind: "event",
      requestId: null,
      feature: "counter",
      name: "first",
      payload: { step: "first" },
      error: null,
    });
    bus.receive({
      scope: "session",
      session: address("a", "a1"),
      kind: "event",
      requestId: null,
      feature: "counter",
      name: "second",
      payload: { step: "second" },
      error: null,
    });
    bus.receive({
      scope: "session",
      session: address("a", "a1"),
      kind: "event",
      requestId: null,
      feature: "other",
      name: "run",
      payload: {},
      error: null,
    });

    await Promise.all([firstEntered.promise, otherEntered.promise]);
    expect(order).toEqual(["first-entered"]);
    releaseFirst.resolve();
    await secondEntered.promise;
    expect(order).toEqual(["first-entered", "first-finished", "second-entered"]);
  });

  it("delivers an event to every subscriber even when one subscriber fails", async () => {
    const bus = new MessageBus("session", address("a", "a1"), () => undefined);
    const delivered = Promise.withResolvers<void>();
    const error = vi.spyOn(console, "error").mockImplementation(() => undefined);
    const feature = bus.feature("dummy");
    feature.on("changed", () => {
      throw new Error("broken subscriber");
    });
    feature.on("changed", () => delivered.resolve());

    bus.receive({
      scope: "session",
      session: address("a", "a1"),
      kind: "event",
      requestId: null,
      feature: "dummy",
      name: "changed",
      payload: {},
      error: null,
    });

    await delivered.promise;
    expect(error).toHaveBeenCalledOnce();
    error.mockRestore();
  });

  it("cancels an in-flight request at its owning handler", async () => {
    let client: MessageBus;
    let host: MessageBus;
    client = new MessageBus("session", address("a", "a1"), (json) => {
      const envelope = parseEnvelope(json);
      if (envelope !== null) {
        host.receive(envelope);
      }
    });
    host = new MessageBus("session", address("a", "a1"), (json) => {
      const envelope = parseEnvelope(json);
      if (envelope !== null) {
        client.receive(envelope);
      }
    });
    const entered = Promise.withResolvers<void>();
    const cancelled = Promise.withResolvers<void>();
    host.feature("dummy").handle("wait", (_payload, signal) => {
      entered.resolve();
      return new Promise((_resolve, reject) => {
        signal.addEventListener(
          "abort",
          () => {
            cancelled.resolve();
            reject(signal.reason);
          },
          { once: true },
        );
      });
    });
    const cancellation = new AbortController();
    const request = client.feature("dummy").request("wait", {}, cancellation.signal);

    await entered.promise;
    cancellation.abort(new Error("stop"));

    await expect(request).rejects.toThrow("stop");
    await cancelled.promise;
  });

  it("settles cancellation even when the disconnected transport cannot send the cancel frame", async () => {
    let failWrites = false;
    const bus = new MessageBus("session", address("a", "a1"), () => {
      if (failWrites) {
        throw new Error("transport closed");
      }
    });
    const cancellation = new AbortController();
    const request = bus.feature("dummy").request("wait", {}, cancellation.signal);

    failWrites = true;
    cancellation.abort(new Error("stop"));

    await expect(request).rejects.toThrow("stop");
  });

  it("does not retry a reply as a failure when its peer has disconnected", async () => {
    let writes = 0;
    const error = vi.spyOn(console, "error").mockImplementation(() => undefined);
    const bus = new MessageBus("session", address("a", "a1"), () => {
      writes += 1;
      throw new Error("transport closed");
    });
    const laneDrained = Promise.withResolvers<void>();
    const feature = bus.feature("dummy");
    feature.handle("read", () => ({ value: 1 }));
    feature.on("after", () => laneDrained.resolve());

    bus.receive({
      scope: "session",
      session: address("a", "a1"),
      kind: "request",
      requestId: "request",
      feature: "dummy",
      name: "read",
      payload: {},
      error: null,
    });
    bus.receive({
      scope: "session",
      session: address("a", "a1"),
      kind: "event",
      requestId: null,
      feature: "dummy",
      name: "after",
      payload: {},
      error: null,
    });

    await laneDrained.promise;
    expect(writes).toBe(1);
    expect(error).toHaveBeenCalledOnce();
    error.mockRestore();
  });

  it("requires cancellation to match the original feature and name", async () => {
    const bus = new MessageBus("session", address("a", "a1"), () => undefined);
    const entered = Promise.withResolvers<void>();
    const release = Promise.withResolvers<void>();
    let cancelled = false;
    bus.feature("dummy").handle("wait", async (_payload, signal) => {
      signal.addEventListener("abort", () => {
        cancelled = true;
      });
      entered.resolve();
      await release.promise;
      return {};
    });
    bus.receive({
      scope: "session",
      session: address("a", "a1"),
      kind: "request",
      requestId: "request",
      feature: "dummy",
      name: "wait",
      payload: {},
      error: null,
    });
    await entered.promise;

    bus.receive({
      scope: "session",
      session: address("a", "a1"),
      kind: "cancel",
      requestId: "request",
      feature: "other",
      name: "wait",
      payload: null,
      error: null,
    });
    bus.receive({
      scope: "session",
      session: address("a", "a1"),
      kind: "cancel",
      requestId: "request",
      feature: "dummy",
      name: "other",
      payload: null,
      error: null,
    });

    expect(cancelled).toBe(false);
    release.resolve();
  });

  it("cannot let a request from a dropped link erase a reused correlation id", async () => {
    const bus = new MessageBus("session", address("a", "a1"), () => undefined);
    const firstEntered = Promise.withResolvers<void>();
    const releaseFirst = Promise.withResolvers<void>();
    const secondEntered = Promise.withResolvers<void>();
    const secondCancelled = Promise.withResolvers<void>();
    bus
      .feature("dummy")
      .handleConcurrent<{ generation: number }, Record<string, never>>(
        "wait",
        async ({ generation }, signal) => {
          if (generation === 1) {
            firstEntered.resolve();
            await releaseFirst.promise;
            return {};
          }
          secondEntered.resolve();
          await new Promise<void>((resolve) =>
            signal.addEventListener(
              "abort",
              () => {
                secondCancelled.resolve();
                resolve();
              },
              { once: true },
            ),
          );
          return {};
        },
      );
    const request = (generation: number) =>
      bus.receive({
        scope: "session",
        session: address("a", "a1"),
        kind: "request",
        requestId: "server-1",
        feature: "dummy",
        name: "wait",
        payload: { generation },
        error: null,
      });

    request(1);
    await firstEntered.promise;
    bus.linkDropped("dropped");
    request(2);
    await secondEntered.promise;
    releaseFirst.resolve();
    await Promise.resolve();
    await Promise.resolve();
    bus.receive({
      scope: "session",
      session: address("a", "a1"),
      kind: "cancel",
      requestId: "server-1",
      feature: "dummy",
      name: "wait",
      payload: null,
      error: null,
    });

    await secondCancelled.promise;
  });

  it("ignores a duplicate request id without replacing the original request", async () => {
    const sent: string[] = [];
    const bus = new MessageBus("session", address("a", "a1"), (json) => sent.push(json));
    const entered = Promise.withResolvers<void>();
    const release = Promise.withResolvers<void>();
    let calls = 0;
    bus.feature("dummy").handleConcurrent("wait", async ({ value }: { value: number }) => {
      calls += 1;
      entered.resolve();
      await release.promise;
      return { value };
    });
    const request = (value: number) =>
      bus.receive({
        scope: "session",
        session: address("a", "a1"),
        kind: "request",
        requestId: "request",
        feature: "dummy",
        name: "wait",
        payload: { value },
        error: null,
      });
    request(1);
    await entered.promise;

    request(2);
    expect(calls).toBe(1);
    expect(sent).toEqual([]);
    release.resolve();
    await Promise.resolve();
    await Promise.resolve();

    expect(sent).toHaveLength(1);
    expect(parseEnvelope(sent[0]!)?.payload).toEqual({ value: 1 });
  });

  it("does not start queued work after its session closes", async () => {
    const bus = new MessageBus("session", address("a", "a1"), () => undefined);
    const entered = Promise.withResolvers<void>();
    const release = Promise.withResolvers<void>();
    const calls: string[] = [];
    const feature = bus.feature("dummy");
    feature.on<{ value: string }>("run", async ({ value }) => {
      calls.push(value);
      if (value === "first") {
        entered.resolve();
        await release.promise;
      }
    });
    const event = (value: string) =>
      bus.receive({
        scope: "session",
        session: address("a", "a1"),
        kind: "event",
        requestId: null,
        feature: "dummy",
        name: "run",
        payload: { value },
        error: null,
      });

    event("first");
    event("second");
    await entered.promise;
    bus.close("removed");
    release.resolve();
    await Promise.resolve();
    await Promise.resolve();

    expect(calls).toEqual(["first"]);
  });
});
