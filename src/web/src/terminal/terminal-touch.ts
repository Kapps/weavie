import type { Terminal } from "@xterm/xterm";

const TAP_MAX_DURATION_MS = 700;
const TAP_MAX_MOVEMENT_PX = 30;

interface TouchStart {
  id: number;
  target: EventTarget;
  time: number;
  x: number;
  y: number;
}

interface TerminalTouchActions {
  click: (target: EventTarget, x: number, y: number) => void;
  focus: () => void;
  mouseTrackingMode: () => Terminal["modes"]["mouseTrackingMode"];
}

interface TerminalGestureChangeEvent extends Event {
  clientX?: number;
  clientY?: number;
  translationX?: number;
  translationY?: number;
}

interface TerminalTouchController {
  onGestureChange: (event: Event) => void;
  onTouchCancel: () => void;
  onTouchEnd: (event: TouchEvent) => void;
  onTouchMove: (event: TouchEvent) => void;
  onTouchStart: (event: TouchEvent) => void;
}

const XTERM_GESTURE_CHANGE = "-xterm-gesturechange";

/** Distinguishes a terminal tap from scrolling and turns mouse-aware TUI taps into real clicks. */
export function createTerminalTouchController(
  actions: TerminalTouchActions,
): TerminalTouchController {
  let start: TouchStart | null = null;
  let activeTouchId: number | null = null;
  let gesturePoint: { x: number; y: number } | null = null;

  const onTouchStart = (event: TouchEvent): void => {
    const touch = event.touches.length === 1 ? event.touches.item(0) : null;
    activeTouchId = touch?.identifier ?? null;
    gesturePoint = touch === null ? null : { x: touch.clientX, y: touch.clientY };
    start =
      touch === null
        ? null
        : {
            id: touch.identifier,
            target: touch.target,
            time: event.timeStamp,
            x: touch.clientX,
            y: touch.clientY,
          };
  };

  const onTouchMove = (event: TouchEvent): void => {
    const activeTouch =
      activeTouchId === null || event.touches.length !== 1
        ? null
        : findTouch(event.touches, activeTouchId);
    if (activeTouch === null) {
      activeTouchId = null;
      gesturePoint = null;
    } else {
      gesturePoint = { x: activeTouch.clientX, y: activeTouch.clientY };
    }

    const origin = start;
    if (origin === null || event.touches.length !== 1) {
      start = null;
      return;
    }
    const touch = findTouch(event.touches, origin.id);
    if (
      touch === null ||
      Math.hypot(touch.clientX - origin.x, touch.clientY - origin.y) > TAP_MAX_MOVEMENT_PX
    ) {
      start = null;
    }
  };

  const onTouchEnd = (event: TouchEvent): void => {
    activeTouchId = null;

    const origin = start;
    start = null;
    if (origin === null || event.touches.length !== 0) {
      return;
    }
    const touch = findTouch(event.changedTouches, origin.id);
    if (
      touch === null ||
      event.timeStamp - origin.time > TAP_MAX_DURATION_MS ||
      Math.hypot(touch.clientX - origin.x, touch.clientY - origin.y) > TAP_MAX_MOVEMENT_PX
    ) {
      return;
    }

    actions.focus();
    if (actions.mouseTrackingMode() !== "none") {
      event.preventDefault();
      actions.click(origin.target, touch.clientX, touch.clientY);
    }
  };

  const onGestureChange = (event: Event): void => {
    const gesture = event as TerminalGestureChangeEvent;
    const clientX = gesture.clientX;
    const clientY = gesture.clientY;
    if (isFiniteNumber(clientX) && isFiniteNumber(clientY)) {
      gesturePoint = { x: clientX, y: clientY };
      return;
    }
    const translationX = gesture.translationX;
    const translationY = gesture.translationY;
    if (gesturePoint === null || !isFiniteNumber(translationX) || !isFiniteNumber(translationY)) {
      event.stopImmediatePropagation();
      throw new Error("Xterm emitted a touch gesture without physical coordinates");
    }

    gesturePoint = {
      x: gesturePoint.x + translationX,
      y: gesturePoint.y + translationY,
    };
    Object.assign(gesture, { clientX: gesturePoint.x, clientY: gesturePoint.y });
  };

  return {
    onTouchCancel: () => {
      start = null;
      activeTouchId = null;
      gesturePoint = null;
    },
    onGestureChange,
    onTouchEnd,
    onTouchMove,
    onTouchStart,
  };
}

/** Installs the capture bridge before xterm consumes its private gesture events. */
export function bindTerminalTouch(
  screen: HTMLElement,
  controller: TerminalTouchController,
): () => void {
  screen.addEventListener("touchstart", controller.onTouchStart, true);
  screen.addEventListener("touchmove", controller.onTouchMove, true);
  screen.addEventListener("touchend", controller.onTouchEnd, true);
  screen.addEventListener("touchcancel", controller.onTouchCancel, true);
  screen.addEventListener(XTERM_GESTURE_CHANGE, controller.onGestureChange, true);

  return () => {
    screen.removeEventListener("touchstart", controller.onTouchStart, true);
    screen.removeEventListener("touchmove", controller.onTouchMove, true);
    screen.removeEventListener("touchend", controller.onTouchEnd, true);
    screen.removeEventListener("touchcancel", controller.onTouchCancel, true);
    screen.removeEventListener(XTERM_GESTURE_CHANGE, controller.onGestureChange, true);
  };
}

/** Sends the same left-button pair xterm receives from a desktop click. */
export function dispatchTerminalMouseTap(target: EventTarget, x: number, y: number): void {
  const common = {
    bubbles: true,
    button: 0,
    cancelable: true,
    clientX: x,
    clientY: y,
    view: window,
  };
  target.dispatchEvent(new MouseEvent("mousedown", { ...common, buttons: 1 }));
  target.dispatchEvent(new MouseEvent("mouseup", { ...common, buttons: 0 }));
}

function findTouch(touches: TouchList, id: number): Touch | null {
  for (let index = 0; index < touches.length; index++) {
    const touch = touches.item(index);
    if (touch?.identifier === id) {
      return touch;
    }
  }
  return null;
}

function isFiniteNumber(value: number | undefined): value is number {
  return typeof value === "number" && Number.isFinite(value);
}
