// The native window's chrome state, host-pushed as `window.state` (a local-machine push: only a shell
// with a real window sends it; browser-served pages never see one and the defaults hold). One source for
// the title bar, the attention intake, and seen-marking.

import { createSignal } from "solid-js";
import { registerHostFeature } from "../bridge";

const [maximized, setMaximized] = createSignal(false);
const [hostFocused, setHostFocused] = createSignal(true);
// Re-runs reactive readers of windowFocused() when the page's own focus or visibility may have changed.
const [pageTick, setPageTick] = createSignal(0, { equals: false });
const tickPage = (): void => {
  setPageTick(0);
};
window.addEventListener("focus", tickPage);
window.addEventListener("blur", tickPage);
document.addEventListener("visibilitychange", tickPage);

registerHostFeature((connection) => {
  if (!connection.isLocal) {
    return;
  }
  return connection.host
    .feature("window")
    .on<{ maximized: boolean; focused: boolean }>("state", ({ maximized, focused }) => {
      setMaximized(maximized);
      setHostFocused(focused);
    });
});

/** Whether the native window is maximized (the title bar's restore glyph + the resize frame). */
export const windowMaximized = maximized;

/** The shell-reported window activation (true where no shell pushes one — browser-served pages). */
export const hostWindowFocused = hostFocused;

/**
 * Whether the user is at this window right now. document.hasFocus() inside WebView2/WKWebView keeps
 * reporting true after the native window is minimized or deactivated, so the shell's window.state event
 * and page visibility corroborate it — any signal saying "away" wins.
 */
export function windowFocused(): boolean {
  pageTick();
  return document.hasFocus() && !document.hidden && hostFocused();
}
