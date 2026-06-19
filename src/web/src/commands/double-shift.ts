// Detects the "double-shift" gesture (tap + release Shift twice in quick succession, IntelliJ-style) and
// fires a callback. This deliberately lives outside the keybinding resolver: a chord is key-plus-modifiers,
// and the resolver explicitly never matches a modifiers-only binding, so a bare double-tap can't be expressed
// there. Any non-Shift key, or any other modifier held alongside Shift, breaks the sequence — so a normal
// Shift+letter (or Ctrl+Shift+…) never trips it; only two clean Shift taps with nothing between them do.
// Capture-phase, like the keybinding resolver, so a focused xterm/Monaco can't swallow the keys first.

const DOUBLE_TAP_WINDOW_MS = 300;

/** Installs the double-shift gesture detector; returns a teardown function. */
export function installDoubleShift(onTrigger: () => void): () => void {
  let armed = false; // a clean Shift keydown is in flight (no other key/modifier has interfered)
  let lastTapAt = 0; // timestamp of the previous completed Shift tap (keyup)

  const reset = (): void => {
    armed = false;
    lastTapAt = 0;
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === "Shift") {
      // Shift held together with another modifier (e.g. Ctrl+Shift) is not the gesture.
      if (event.ctrlKey || event.metaKey || event.altKey) {
        reset();
        return;
      }
      armed = true;
      return;
    }
    // Any other key cancels an in-progress sequence.
    reset();
  };

  const onKeyUp = (event: KeyboardEvent): void => {
    if (event.key !== "Shift" || !armed) {
      return;
    }
    armed = false;
    const now = event.timeStamp;
    if (lastTapAt !== 0 && now - lastTapAt <= DOUBLE_TAP_WINDOW_MS) {
      reset();
      onTrigger();
      return;
    }
    lastTapAt = now;
  };

  window.addEventListener("keydown", onKeyDown, { capture: true });
  window.addEventListener("keyup", onKeyUp, { capture: true });
  window.addEventListener("blur", reset);
  return () => {
    window.removeEventListener("keydown", onKeyDown, { capture: true });
    window.removeEventListener("keyup", onKeyUp, { capture: true });
    window.removeEventListener("blur", reset);
  };
}
