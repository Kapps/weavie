// Detects the "double-shift" gesture (tap + release Shift twice quickly, IntelliJ-style) and fires a
// callback. Outside the keybinding resolver, which never matches a modifiers-only chord. Any other key or
// modifier alongside Shift breaks the sequence. Capture-phase so a focused xterm/Monaco can't swallow it.

const DOUBLE_TAP_WINDOW_MS = 300;

/** Installs the double-shift gesture detector; returns a teardown function. */
export function installDoubleShift(onTrigger: () => void): () => void {
  let armed = false; // a clean Shift keydown is in flight (no other key/modifier has interfered)
  let lastTapAt = 0; // timestamp of the previous completed Shift tap

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
