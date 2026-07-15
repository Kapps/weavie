// The native window's chrome state, host-pushed as `window-state` (a local-machine push: only a shell
// with a real window sends it; browser-served pages never see one and the defaults hold). One source for
// the title bar and the attention intake.

import { createSignal } from "solid-js";
import { onHostMessage } from "../bridge";

const [maximized, setMaximized] = createSignal(false);
const [hostFocused, setHostFocused] = createSignal(true);

onHostMessage((message) => {
  if (message.type === "window-state") {
    setMaximized(message.maximized);
    setHostFocused(message.focused);
  }
});

/** Whether the native window is maximized (the title bar's restore glyph + the resize frame). */
export const windowMaximized = maximized;

/** The shell-reported window activation (true where no shell pushes one — browser-served pages). */
export const hostWindowFocused = hostFocused;

/**
 * Whether the user is at this window right now. document.hasFocus() inside WebView2/WKWebView keeps
 * reporting true after the native window is minimized or deactivated, so the shell's window-state push
 * and page visibility corroborate it — any signal saying "away" wins.
 */
export function windowFocused(): boolean {
  return document.hasFocus() && !document.hidden && hostFocused();
}
