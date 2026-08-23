// Detects the "double-shift" gesture (tap + release Shift twice quickly, IntelliJ-style) and fires a
// callback. Outside the keybinding resolver, which never matches a modifiers-only chord. Any other key or
// modifier alongside Shift, or any mouse click, breaks the sequence. Capture-phase so a focused
// xterm/Monaco can't swallow it.

import { evaluateWhen } from "./context";

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
    if (evaluateWhen("modalOpen")) {
      reset();
      return;
    }
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
    if (evaluateWhen("modalOpen")) {
      reset();
      return;
    }
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

  // A Shift held for a mouse gesture (e.g. shift-click to extend a selection) is a modifier chord, not a
  // tap — without this, two shift-clicks landing within the tap window (or the down/up pair around a
  // single one) satisfy the same timing the gesture watches and steal focus to the omnibar mid-selection.
  // Flaked as comment-prose-selection.spec.ts's "leaves it raw" losing editor focus after a shift-click,
  // 2026-08-23 (https://github.com/Kapps/weavie/actions/runs/32616592610/job/97139307788): traced via a
  // focus/blur/key event log to the shift-click's own Shift down/up landing inside the 300ms double-tap
  // window and firing focusOmnibarFiles, unrelated to the editor code the test's diff touches.
  const onMouseDown = (): void => reset();

  window.addEventListener("keydown", onKeyDown, { capture: true });
  window.addEventListener("keyup", onKeyUp, { capture: true });
  window.addEventListener("mousedown", onMouseDown, { capture: true });
  window.addEventListener("blur", reset);
  return () => {
    window.removeEventListener("keydown", onKeyDown, { capture: true });
    window.removeEventListener("keyup", onKeyUp, { capture: true });
    window.removeEventListener("mousedown", onMouseDown, { capture: true });
    window.removeEventListener("blur", reset);
  };
}
