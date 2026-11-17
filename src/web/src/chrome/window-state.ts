// The native window's chrome: its description (boot config, re-pushed as `window.chrome`) and its state
// (`window.state`). Local-machine pushes only: a browser-served page keeps its boot description and the
// state defaults. One source for the title bar, the attention intake, and seen-marking.

import { createSignal } from "solid-js";
import { registerHostFeature } from "../bridge";

const [chrome, setChrome] = createSignal<WeavieWindowChrome | null>(
  window.__WEAVIE_SHELL__?.chrome ?? window.__WEAVIE_WELCOME__?.chrome ?? null,
);
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
  const feature = connection.host.feature("window");
  const offState = feature.on<{ maximized: boolean; focused: boolean }>(
    "state",
    ({ maximized, focused }) => {
      setMaximized(maximized);
      setHostFocused(focused);
    },
  );
  const offChrome = feature.on<WeavieWindowChrome>("chrome", setChrome);
  return () => {
    offState();
    offChrome();
  };
});

/** What the title bar draws for this window, or null when no host describes one (plain-browser dev). */
export const windowChrome = chrome;

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
