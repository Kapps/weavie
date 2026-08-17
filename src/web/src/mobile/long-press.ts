import { onCleanup } from "solid-js";

const HOLD_MS = 500;
const HOLD_SLOP_PX = 10;

/** The element handlers a hold gesture needs; spread onto the element that owns the menu. */
export interface HoldToOpenHandlers {
  onContextMenu: (event: MouseEvent) => void;
  onPointerCancel: () => void;
  onPointerDown: (event: PointerEvent) => void;
  onPointerMove: (event: PointerEvent) => void;
  onPointerUp: () => void;
}

/**
 * Touch chrome's stand-in for right-click: holding still opens the menu over whatever the press started on,
 * and the click that same touch synthesizes on release is swallowed so it can't land on the menu that just
 * appeared under the finger. Attach it to the element that outlives the gesture (a list, not its rows), and
 * resolve the target from `pressed`. `open` reports whether it took the press, so a surface with no menu to
 * give leaves the platform's own behavior alone.
 */
export function holdToOpen(
  open: (x: number, y: number, pressed: EventTarget | null) => boolean,
): HoldToOpenHandlers {
  let timer = 0;
  let press: { x: number; y: number; target: EventTarget | null } | null = null;
  let opened = false;

  const swallowClick = (event: MouseEvent): void => {
    event.preventDefault();
    event.stopPropagation();
  };
  const stopSwallowing = (): void => {
    window.removeEventListener("click", swallowClick, true);
  };
  const cancel = (): void => {
    window.clearTimeout(timer);
    press = null;
  };
  // The one open path: a touch hold arms the swallow, a mouse right-click (which synthesizes no click) doesn't.
  const raise = (x: number, y: number, target: EventTarget | null, touch: boolean): boolean => {
    opened = open(x, y, target);
    if (opened && touch) {
      window.addEventListener("click", swallowClick, { capture: true, once: true });
    }
    return opened;
  };
  onCleanup(() => {
    cancel();
    stopSwallowing();
  });

  return {
    onPointerDown: (event) => {
      stopSwallowing();
      opened = false;
      if (event.pointerType === "mouse") {
        return;
      }
      press = { x: event.clientX, y: event.clientY, target: event.target };
      timer = window.setTimeout(() => {
        const point = press;
        cancel();
        if (point !== null) {
          raise(point.x, point.y, point.target, true);
        }
      }, HOLD_MS);
    },
    onPointerMove: (event) => {
      if (
        press !== null &&
        Math.hypot(event.clientX - press.x, event.clientY - press.y) > HOLD_SLOP_PX
      ) {
        cancel();
      }
    },
    onPointerUp: cancel,
    // A canceled touch never synthesizes a click, so the armed swallow would otherwise eat the next real tap.
    onPointerCancel: () => {
      cancel();
      stopSwallowing();
    },
    // The platform's own long-press can beat HOLD_MS on Android, so this path arms the swallow the same way.
    onContextMenu: (event) => {
      const touch = press !== null;
      cancel();
      if (opened || raise(event.clientX, event.clientY, event.target, touch)) {
        event.preventDefault();
      }
    },
  };
}
