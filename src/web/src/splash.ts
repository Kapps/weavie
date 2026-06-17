// Controls the pre-JS splash (the #splash element painted from index.html before any script runs).
// Rather than removing it the instant the shell mounts — which exposed a relay of placeholders
// (wordmark → "loading editor" → editor pop-in, each its own flash) — we hold the splash over the
// whole app until it's genuinely ready, then fade once. The user sees a single dark → app reveal.

let dismissed = false;

/** Fades out and removes the splash. Idempotent and safe to call before/after the element exists. */
export function dismissSplash(): void {
  if (dismissed) {
    return;
  }
  dismissed = true;

  const splash = document.getElementById("splash");
  if (splash === null) {
    return;
  }

  // Let the just-mounted UI paint one frame *under* the opaque splash, so the fade reveals a settled
  // screen (editor created, terminals laid out) instead of catching a mid-render flash.
  requestAnimationFrame(() => {
    splash.classList.add("hide");
    const remove = (): void => splash.remove();
    splash.addEventListener("transitionend", remove, { once: true });
    // Belt-and-braces: remove even if transitionend never fires (reduced-motion, display quirks).
    window.setTimeout(remove, 400);
  });
}
