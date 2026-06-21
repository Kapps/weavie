// Controls the pre-JS splash (the #splash element painted from index.html before any script runs).
// Held over the whole app until it's genuinely ready, then dropped, so the user sees a single dark → app
// reveal rather than a relay of placeholder flashes.

let dismissed = false;

/**
 * Removes the splash immediately. Idempotent and safe to call before/after the element exists.
 *
 * The caller decides *when* (once the app is genuinely ready), so the app's DOM is already settled and
 * removal reveals it with no mid-render flash. No fade: a fade is pure startup latency and relies on
 * requestAnimationFrame, which WKWebView pauses for an occluded window — leaving the splash stuck over an
 * already-ready app when a launch never gets focus.
 */
export function dismissSplash(): void {
  if (dismissed) {
    return;
  }
  dismissed = true;
  document.getElementById("splash")?.remove();
}
