import { onCleanup } from "solid-js";

/**
 * Dismisses an open popover once the user's attention leaves it: a pointer-down anywhere outside `inside` (a
 * selector list covering the popover plus its own toggle, which owns that click itself), or the window losing
 * focus. Capture phase, so it still lands when a handler under the pointer stops propagation. Registered for
 * the calling reactive scope — call it inside an open-gated effect to listen only while the popover is open.
 */
export function dismissOnOutsideInteraction(inside: string, close: () => void): void {
  const onPointerDown = (event: PointerEvent): void => {
    const target = event.target;
    if (!(target instanceof Element) || target.closest(inside) === null) {
      close();
    }
  };
  window.addEventListener("pointerdown", onPointerDown, { capture: true });
  window.addEventListener("blur", close);
  onCleanup(() => {
    window.removeEventListener("pointerdown", onPointerDown, { capture: true });
    window.removeEventListener("blur", close);
  });
}
