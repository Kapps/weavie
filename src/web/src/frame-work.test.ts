import { afterEach, beforeEach, expect, test, vi } from "vitest";

let frames: (() => void)[];
let messages: (() => void)[];
let listeners: Map<string, () => void>;
let work: typeof import("./frame-work");

// Draws one frame: its frame callbacks, then the messages they posted, then the promises those settled.
const frame = async (): Promise<void> => {
  for (const run of frames.splice(0)) run();
  for (const deliver of messages.splice(0)) deliver();
  await Promise.resolve();
};

beforeEach(async () => {
  vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout", "performance"] });
  frames = [];
  messages = [];
  listeners = new Map();
  vi.stubGlobal("requestAnimationFrame", (run: () => void) => frames.push(run));
  vi.stubGlobal(
    "MessageChannel",
    class {
      port1: { onmessage?: () => void } = {};
      port2 = { postMessage: () => messages.push(() => this.port1.onmessage?.()) };
    },
  );
  vi.stubGlobal("addEventListener", (type: string, listener: () => void) =>
    listeners.set(type, listener),
  );
  vi.stubGlobal("requestIdleCallback", undefined);
  vi.stubGlobal("cancelIdleCallback", undefined);
  vi.resetModules();
  work = await import("./frame-work");
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

test("stages run after a frame has drawn, one per frame in the order they asked", async () => {
  const ran: string[] = [];
  void work.afterFrame(() => true).then(() => ran.push("first"));
  void work.afterFrame(() => true).then(() => ran.push("second"));
  await Promise.resolve();
  expect(ran).toEqual([]);
  await frame();
  expect(ran).toEqual(["first"]);
  await frame();
  expect(ran).toEqual(["first", "second"]);
});

test("a stage no longer wanted is let go without taking the frame", async () => {
  const ran: string[] = [];
  let wanted = true;
  void work.afterFrame(() => wanted).then(() => ran.push("dropped"));
  void work.afterFrame(() => true).then(() => ran.push("kept"));
  wanted = false;
  await frame();
  expect(ran).toEqual(["dropped", "kept"]);
});

test("idle work runs in a frame no stage took", async () => {
  work.installIdleCallback();
  const ran: string[] = [];
  requestIdleCallback(() => ran.push("idle"));
  void work.afterFrame(() => true).then(() => ran.push("stage"));
  await frame();
  expect(ran).toEqual(["stage"]);
  await frame();
  expect(ran).toEqual(["stage", "idle"]);
});

test("idle callbacks share one frame's budget", async () => {
  work.installIdleCallback();
  const ran: string[] = [];
  requestIdleCallback((deadline) => {
    expect(deadline.didTimeout).toBe(false);
    expect(deadline.timeRemaining()).toBe(10);
    vi.advanceTimersByTime(10);
    ran.push("long");
  });
  requestIdleCallback(() => ran.push("next"));
  await frame();
  expect(ran).toEqual(["long"]);
  await frame();
  expect(ran).toEqual(["long", "next"]);
});

test("idle work waits for scrolling to settle", async () => {
  work.installIdleCallback();
  const ran: string[] = [];
  requestIdleCallback(() => ran.push("idle"));
  for (const scroll of [listeners.get("wheel")!, listeners.get("touchmove")!, work.noteScroll]) {
    scroll();
    vi.advanceTimersByTime(249);
    await frame();
    expect(ran).toEqual([]);
  }
  vi.advanceTimersByTime(1);
  await frame();
  expect(ran).toEqual(["idle"]);
});

test("an idle callback that asks again waits for a later frame", async () => {
  work.installIdleCallback();
  let runs = 0;
  const again = (): void => {
    if (++runs < 3) requestIdleCallback(again);
  };
  requestIdleCallback(again);
  await frame();
  expect(runs).toBe(1);
  await frame();
  expect(runs).toBe(2);
});

test("an idle callback that throws does not strand the ones behind it", async () => {
  work.installIdleCallback();
  const ran: string[] = [];
  requestIdleCallback(() => {
    throw new Error("broken");
  });
  requestIdleCallback(() => ran.push("next"));
  await expect(frame()).rejects.toThrow("broken");
  await frame();
  expect(ran).toEqual(["next"]);
});

test("an idle callback's timeout runs it mid-scroll, once", async () => {
  work.installIdleCallback();
  const timedOut: boolean[] = [];
  requestIdleCallback((deadline) => timedOut.push(deadline.didTimeout), { timeout: 50 });
  listeners.get("wheel")!();
  await frame();
  expect(timedOut).toEqual([]);
  vi.advanceTimersByTime(50);
  expect(timedOut).toEqual([true]);
  vi.advanceTimersByTime(250);
  await frame();
  expect(timedOut).toEqual([true]);
});

test("a cancelled idle callback never runs", async () => {
  work.installIdleCallback();
  const ran: string[] = [];
  cancelIdleCallback(requestIdleCallback(() => ran.push("cancelled"), { timeout: 50 }));
  requestIdleCallback(() => ran.push("kept"));
  vi.advanceTimersByTime(50);
  await frame();
  expect(ran).toEqual(["kept"]);
});

test("an engine's own idle scheduler is kept", () => {
  const native = vi.fn();
  vi.stubGlobal("requestIdleCallback", native);
  work.installIdleCallback();
  expect(requestIdleCallback).toBe(native);
  expect(listeners.size).toBe(0);
});
