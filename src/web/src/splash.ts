// Controls the pre-JS splash (the #splash element from index.html), held until the app is genuinely ready
// then dropped, so the user sees a single dark → app reveal rather than placeholder flashes.

let dismissed = false;
const listeners = new Set<() => void>();

/** Runs `listener` when the startup splash is gone, immediately if it already is. */
export function onSplashDismissed(listener: () => void): () => void {
  if (dismissed || document.getElementById("splash") === null) {
    listener();
    return () => {};
  }
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/**
 * Removes the splash immediately. Idempotent and safe before/after the element exists. No fade: it relies
 * on requestAnimationFrame, which WKWebView pauses for an occluded window — stranding an unfocused launch.
 */
export function dismissSplash(): void {
  if (dismissed) {
    return;
  }
  dismissed = true;
  document.getElementById("splash")?.remove();
  const pending = [...listeners];
  listeners.clear();
  for (const listener of pending) {
    listener();
  }
}
