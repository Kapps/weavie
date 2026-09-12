import { describe, expect, it, vi } from "vitest";
import { DeferredFocus } from "./deferred-focus";

function harness() {
  const input = new EventTarget();
  const callbacks = new Map<number, FrameRequestCallback>();
  let handle = 0;
  let owner = "a";
  const focus = new DeferredFocus(
    () => owner,
    {
      request: (callback) => {
        callbacks.set(++handle, callback);
        return handle;
      },
      cancel: (id) => callbacks.delete(id),
    },
    input,
  );
  return {
    focus,
    input,
    callbacks,
    select: (next: string) => {
      owner = next;
      focus.invalidate();
    },
    flush: () => {
      const pending = [...callbacks.values()];
      callbacks.clear();
      for (const callback of pending) callback(0);
    },
  };
}

describe("deferred focus ownership", () => {
  it("runs the current intent once and cancels an earlier frame", () => {
    const { focus, flush, callbacks } = harness();
    const old = vi.fn();
    const current = vi.fn();
    focus.schedule("a", old);
    focus.schedule("a", current);
    expect(callbacks.size).toBe(1);
    flush();
    flush();
    expect(old).not.toHaveBeenCalled();
    expect(current).toHaveBeenCalledOnce();
  });

  it("completes an asynchronous intent immediately and only once", () => {
    const { focus, callbacks } = harness();
    const action = vi.fn();
    const complete = focus.capture("a");
    complete(action);
    complete(action);
    expect(action).toHaveBeenCalledOnce();
    expect(callbacks.size).toBe(0);
  });

  it.each([
    "pointerdown",
    "keydown",
    "focusin",
  ])("retires both queued frames and asynchronous tickets on %s", (event) => {
    const { focus, input, flush } = harness();
    const action = vi.fn();
    focus.schedule("a", action);
    input.dispatchEvent(new Event(event));
    flush();
    const complete = focus.capture("a");
    input.dispatchEvent(new Event(event));
    complete(action);
    flush();
    expect(action).not.toHaveBeenCalled();
  });

  it("retires an intent across a switch away and back to its owner", () => {
    const { focus, select, flush } = harness();
    const action = vi.fn();
    const complete = focus.capture("a");
    select("b");
    select("a");
    complete(action);
    flush();
    expect(action).not.toHaveBeenCalled();
  });

  it("refuses focus requested by a background owner", () => {
    const { focus, flush } = harness();
    const action = vi.fn();
    const selected = vi.fn();
    focus.schedule("a", selected);
    focus.schedule("b", action);
    flush();
    expect(action).not.toHaveBeenCalled();
    expect(selected).toHaveBeenCalledOnce();
  });

  it("cannot execute a delivered stale callback or schedule after disposal", () => {
    const { focus, callbacks, flush } = harness();
    const action = vi.fn();
    focus.schedule("a", action);
    const delivered = [...callbacks.values()][0]!;
    focus.dispose();
    delivered(0);
    focus.schedule("a", action);
    flush();
    expect(action).not.toHaveBeenCalled();
  });
});
