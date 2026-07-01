// Write text to the OS clipboard, across both shells. On a browser-hosted shell (headless/remote) the OS
// clipboard is the browser's, written via navigator.clipboard during the user gesture; the native WebView's
// navigator.clipboard is focus- and permission-gated, so it routes through the host, which owns the real OS
// clipboard. Shared by terminal copy (OSC 52 / selection) and the editor tab "Copy" menu.

import { isBrowserHostedShell, postToLocalHost } from "./bridge";

/** Writes text to the OS clipboard via the browser (browser-hosted shell) or the host (native WebView). */
export function writeClipboard(text: string): void {
  if (text.length === 0) {
    return;
  }
  if (isBrowserHostedShell()) {
    // Best-effort: a copy keypress / menu click is a user gesture (allowed), while an OSC 52 write isn't, so
    // the browser may reject that one — fine, the gesture path is what matters.
    void navigator.clipboard?.writeText(text).catch(() => {});
    return;
  }
  // Always the LOCAL host: the clipboard lives on the user's machine, not on whichever (possibly remote)
  // backend drives the page — a remote headless host would silently drop the write.
  postToLocalHost({ type: "clipboard-write", text });
}
