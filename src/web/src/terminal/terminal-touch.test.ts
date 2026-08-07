import { describe, expect, it, vi } from "vitest";
import { createTerminalTouchController } from "./terminal-touch";

interface TouchPoint {
  identifier: number;
  clientX: number;
  clientY: number;
  target: EventTarget;
}

function touchList(...touches: TouchPoint[]): TouchList {
  return {
    ...touches,
    length: touches.length,
    item: (index: number) => touches[index] ?? null,
  } as unknown as TouchList;
}

function touchEvent(
  timeStamp: number,
  touches: TouchPoint[],
  changedTouches: TouchPoint[],
): TouchEvent & { preventDefault: ReturnType<typeof vi.fn> } {
  return {
    changedTouches: touchList(...changedTouches),
    preventDefault: vi.fn(),
    timeStamp,
    touches: touchList(...touches),
  } as unknown as TouchEvent & { preventDefault: ReturnType<typeof vi.fn> };
}

function gestureEvent(
  clientX: number | undefined,
  clientY: number | undefined,
  translationX: number,
  translationY: number,
): Event & {
  clientX?: number;
  clientY?: number;
  stopImmediatePropagation: ReturnType<typeof vi.fn>;
} {
  return {
    clientX,
    clientY,
    stopImmediatePropagation: vi.fn(),
    translationX,
    translationY,
  } as unknown as Event & {
    clientX?: number;
    clientY?: number;
    stopImmediatePropagation: ReturnType<typeof vi.fn>;
  };
}

function fixture(mode: "none" | "vt200") {
  const focus = vi.fn();
  const click = vi.fn();
  const controller = createTerminalTouchController({
    click,
    focus,
    mouseTrackingMode: () => mode,
  });
  const target = new EventTarget();
  const point = (x: number, y: number, identifier: number): TouchPoint => ({
    clientX: x,
    clientY: y,
    identifier,
    target,
  });
  return { click, controller, focus, point, target };
}

describe("terminal touch controller", () => {
  it("focuses a stationary tap without inventing a click when mouse tracking is off", () => {
    const { click, controller, focus, point } = fixture("none");
    controller.onTouchStart(touchEvent(100, [point(20, 30, 1)], []));
    const end = touchEvent(200, [], [point(22, 32, 1)]);

    controller.onTouchEnd(end);

    expect(focus).toHaveBeenCalledOnce();
    expect(click).not.toHaveBeenCalled();
    expect(end.preventDefault).not.toHaveBeenCalled();
  });

  it("turns a mouse-aware TUI tap into one click at the released coordinates", () => {
    const { click, controller, focus, point, target } = fixture("vt200");
    controller.onTouchStart(touchEvent(100, [point(20, 30, 1)], []));
    const end = touchEvent(200, [], [point(22, 32, 1)]);

    controller.onTouchEnd(end);

    expect(focus).toHaveBeenCalledOnce();
    expect(end.preventDefault).toHaveBeenCalledOnce();
    expect(click).toHaveBeenCalledWith(target, 22, 32);
  });

  it("rejects a swipe even when it returns near its starting point", () => {
    const { click, controller, focus, point } = fixture("vt200");
    controller.onTouchStart(touchEvent(100, [point(20, 30, 1)], []));
    controller.onTouchMove(touchEvent(150, [point(80, 30, 1)], [point(80, 30, 1)]));
    controller.onTouchEnd(touchEvent(200, [], [point(22, 31, 1)]));

    expect(focus).not.toHaveBeenCalled();
    expect(click).not.toHaveBeenCalled();
  });

  it("rejects long presses, cancellations, and multitouch", () => {
    const { click, controller, focus, point } = fixture("vt200");
    controller.onTouchStart(touchEvent(100, [point(20, 30, 1)], []));
    controller.onTouchEnd(touchEvent(801, [], [point(20, 30, 1)]));

    controller.onTouchStart(touchEvent(900, [point(20, 30, 1)], []));
    controller.onTouchCancel();
    controller.onTouchEnd(touchEvent(950, [], [point(20, 30, 1)]));

    controller.onTouchStart(touchEvent(1000, [point(20, 30, 1), point(40, 50, 2)], []));
    controller.onTouchEnd(touchEvent(1050, [], [point(20, 30, 1)]));

    expect(focus).not.toHaveBeenCalled();
    expect(click).not.toHaveBeenCalled();
  });

  it("preserves physical gesture coordinates and advances inertia from them", () => {
    const { controller } = fixture("vt200");
    const physical = gestureEvent(40, 70, 0, -10);
    controller.onGestureChange(physical);

    const firstInertia = gestureEvent(undefined, undefined, 2, -5);
    controller.onGestureChange(firstInertia);
    const secondInertia = gestureEvent(undefined, undefined, -1, -3);
    controller.onGestureChange(secondInertia);

    expect(physical).toMatchObject({ clientX: 40, clientY: 70 });
    expect(firstInertia).toMatchObject({ clientX: 42, clientY: 65 });
    expect(secondInertia).toMatchObject({ clientX: 41, clientY: 62 });
  });

  it("starts inertia at xterm's last physical move point", () => {
    const { controller, point } = fixture("vt200");
    controller.onTouchStart(touchEvent(100, [point(40, 100, 1)], []));
    controller.onTouchMove(touchEvent(150, [point(40, 50, 1)], [point(40, 50, 1)]));
    controller.onTouchEnd(touchEvent(200, [], [point(40, 45, 1)]));
    const inertia = gestureEvent(undefined, undefined, 0, -4);

    controller.onGestureChange(inertia);

    expect(inertia).toMatchObject({ clientX: 40, clientY: 46 });
  });

  it("rejects coordinate-less gestures without a preceding touch", () => {
    const { controller } = fixture("vt200");
    const inertia = gestureEvent(undefined, undefined, 0, -4);

    expect(() => controller.onGestureChange(inertia)).toThrow(
      "Xterm emitted a touch gesture without physical coordinates",
    );
    expect(inertia.stopImmediatePropagation).toHaveBeenCalledOnce();
  });
});
