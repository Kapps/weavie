// Routes the back/forward mouse buttons (MouseEvent.button 3 = back, 4 = forward) to the navigation commands
// and prevents the browser/webview's own history navigation on those buttons. Capture-phase so a focused
// xterm/Monaco can't swallow the press. A gesture, like double-shift — outside the chord resolver, which only
// matches keyboard chords.

/** Installs the back/forward mouse-button handler; returns a teardown function. */
export function installMouseNav(onBack: () => void, onForward: () => void): () => void {
  // The back/forward buttons fire mousedown/mouseup/auxclick; cancelling all three stops whichever phase an
  // engine would navigate on, while we act once on the press.
  const suppress = (event: MouseEvent): boolean => {
    if (event.button !== 3 && event.button !== 4) {
      return false;
    }
    event.preventDefault();
    event.stopPropagation();
    return true;
  };
  const onMouseDown = (event: MouseEvent): void => {
    if (!suppress(event)) {
      return;
    }
    if (event.button === 3) {
      onBack();
    } else {
      onForward();
    }
  };
  window.addEventListener("mousedown", onMouseDown, { capture: true });
  window.addEventListener("mouseup", suppress, { capture: true });
  window.addEventListener("auxclick", suppress, { capture: true });
  return () => {
    window.removeEventListener("mousedown", onMouseDown, { capture: true });
    window.removeEventListener("mouseup", suppress, { capture: true });
    window.removeEventListener("auxclick", suppress, { capture: true });
  };
}
