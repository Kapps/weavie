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
  it("runs the current recovery once and cancels an earlier frame", () => {
    const { focus, flush, callbacks } = harness();
    const old = vi.fn();
    const current = vi.fn();
    focus.recover("a", old);
    focus.recover("a", current);
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
    complete.complete(action);
    complete.complete(action);
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
    focus.recover("a", action);
    input.dispatchEvent(new Event(event));
    flush();
    const complete = focus.capture("a");
    input.dispatchEvent(new Event(event));
    complete.complete(action);
    flush();
    expect(action).not.toHaveBeenCalled();
  });

  it("retires an intent across a switch away and back to its owner", () => {
    const { focus, select, flush } = harness();
    const action = vi.fn();
    const complete = focus.capture("a");
    select("b");
    select("a");
    complete.complete(action);
    flush();
    expect(action).not.toHaveBeenCalled();
  });

  it("refuses focus requested by a background owner", () => {
    const { focus, flush } = harness();
    const action = vi.fn();
    const selected = vi.fn();
    focus.recover("a", selected);
    focus.recover("b", action);
    flush();
    expect(action).not.toHaveBeenCalled();
    expect(selected).toHaveBeenCalledOnce();
  });

  it("cannot execute a delivered stale callback or schedule after disposal", () => {
    const { focus, callbacks, flush } = harness();
    const action = vi.fn();
    focus.recover("a", action);
    const delivered = [...callbacks.values()][0]!;
    focus.dispose();
    delivered(0);
    focus.recover("a", action);
    flush();
    expect(action).not.toHaveBeenCalled();
  });
  it("passive recovery cannot cancel or run ahead of an explicit completion", () => {
    const { focus, flush } = harness();
    const intent = focus.capture("a");
    const recovery = vi.fn();
    const explicit = vi.fn();
    focus.recover("a", recovery);
    flush();
    expect(recovery).not.toHaveBeenCalled();
    intent.complete(explicit);
    flush();
    expect(explicit).toHaveBeenCalledOnce();
    expect(recovery).not.toHaveBeenCalled();
  });

  it("releases passive recovery when asynchronous work fails", () => {
    const { focus, flush } = harness();
    const intent = focus.capture("a");
    const recovery = vi.fn();
    focus.recover("a", recovery);
    intent.dispose();
    flush();
    expect(recovery).toHaveBeenCalledOnce();
  });

  it("a stale disposal cannot release a newer intent", () => {
    const { focus, flush } = harness();
    const old = focus.capture("a");
    const current = focus.capture("a");
    const recovery = vi.fn();
    focus.recover("a", recovery);
    old.dispose();
    flush();
    expect(recovery).not.toHaveBeenCalled();
    current.dispose();
    flush();
    expect(recovery).toHaveBeenCalledOnce();
  });
  it("new input cancels recovery waiting behind an explicit intent", () => {
    const { focus, input, flush } = harness();
    const intent = focus.capture("a");
    const recovery = vi.fn();
    focus.recover("a", recovery);
    input.dispatchEvent(new Event("keydown"));
    intent.dispose();
    flush();
    expect(recovery).not.toHaveBeenCalled();
  });

  it("a delivered stale recovery cannot consume its replacement", () => {
    const { focus, callbacks, flush } = harness();
    const old = vi.fn();
    const current = vi.fn();
    focus.recover("a", old);
    const delivered = [...callbacks.values()][0]!;
    focus.recover("a", current);
    delivered(0);
    flush();
    expect(old).not.toHaveBeenCalled();
    expect(current).toHaveBeenCalledOnce();
  });
});
