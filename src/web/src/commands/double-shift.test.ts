import { afterEach, describe, expect, it, vi } from "vitest";
import { setContext } from "./context";
import { installDoubleShift } from "./double-shift";

function keyboardEvent(type: string, key: string, timeStamp: number): Event {
  const event = new Event(type);
  Object.defineProperties(event, {
    key: { value: key },
    ctrlKey: { value: false },
    metaKey: { value: false },
    altKey: { value: false },
    timeStamp: { value: timeStamp },
  });
  return event;
}

function shiftTap(target: EventTarget, timeStamp: number): void {
  target.dispatchEvent(keyboardEvent("keydown", "Shift", timeStamp - 1));
  target.dispatchEvent(keyboardEvent("keyup", "Shift", timeStamp));
}

describe("installDoubleShift", () => {
  afterEach(() => vi.unstubAllGlobals());

  it("triggers for two keyboard-only Shift taps", () => {
    const target = new EventTarget();
    vi.stubGlobal("window", target);
    setContext("modalOpen", false);
    const trigger = vi.fn();
    const dispose = installDoubleShift(trigger);

    shiftTap(target, 100);
    shiftTap(target, 200);

    expect(trigger).toHaveBeenCalledOnce();
    dispose();
  });

  it("does not count Shift used for a mouse gesture as a tap", () => {
    const target = new EventTarget();
    vi.stubGlobal("window", target);
    setContext("modalOpen", false);
    const trigger = vi.fn();
    const dispose = installDoubleShift(trigger);

    shiftTap(target, 100);
    target.dispatchEvent(keyboardEvent("keydown", "Shift", 199));
    target.dispatchEvent(new Event("mousedown"));
    target.dispatchEvent(keyboardEvent("keyup", "Shift", 200));

    expect(trigger).not.toHaveBeenCalled();
    dispose();
  });
});
