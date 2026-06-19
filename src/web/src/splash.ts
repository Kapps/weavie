// Controls the pre-JS splash (the #splash element painted from index.html before any script runs).
// Rather than removing it the instant the shell mounts — which exposed a relay of placeholders
// (wordmark → "loading editor" → editor pop-in, each its own flash) — we hold the splash over the
// whole app until it's genuinely ready, then drop it. The user sees a single dark → app reveal.

let dismissed = false;

/**
 * Removes the splash immediately. Idempotent and safe to call before/after the element exists.
 *
 * The caller decides *when* to call this (once the app is genuinely ready), so by this point the
 * app's DOM is already in its settled state — removing the splash synchronously reveals it with no
 * mid-render flash and nothing to animate. We deliberately don't fade: a fade is pure startup
 * latency, and it relied on requestAnimationFrame, which WKWebView *pauses* for an occluded window,
 * leaving the splash stuck over an already-ready app when a launch never gets focus.
 */
export function dismissSplash(): void {
  if (dismissed) {
    return;
  }
  dismissed = true;
  document.getElementById("splash")?.remove();
}
