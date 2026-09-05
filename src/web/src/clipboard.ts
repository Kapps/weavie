// Write text to the OS clipboard, across both shells. On a browser-hosted shell (headless/remote) the OS
// clipboard is the browser's, written via navigator.clipboard during the user gesture; the native WebView's
// navigator.clipboard is focus- and permission-gated, so it routes through the host, which owns the real OS
// clipboard. Shared by terminal copy (OSC 52 / selection) and the editor tab "Copy" menu.

import { hostConnection, isBrowserHostedShell, LOCAL_BACKEND_ID } from "./bridge";

/** Writes text to the OS clipboard via the browser (browser-hosted shell) or the host (native WebView). */
export function writeClipboard(text: string): void {
  if (text.length === 0) {
    return;
  }
  text = text.trim();
  if (isBrowserHostedShell()) {
    // Best-effort: a copy keypress / menu click is a user gesture (allowed), while an OSC 52 write isn't, so
    // the browser may reject that one — fine, the gesture path is what matters.
    void navigator.clipboard?.writeText(text).catch(() => {});
    return;
  }
  // Always the LOCAL host: the clipboard lives on the user's machine, not on whichever (possibly remote)
  // backend drives the page — a remote headless host would silently drop the write.
  hostConnection(LOCAL_BACKEND_ID)?.host.feature("clipboard").publish("write", { text });
}

/** Applies the text-copy policy after editor/terminal handlers have populated the clipboard. */
export function installClipboardTrimming(): () => void {
  const copy = (event: ClipboardEvent) => {
    const data = event.clipboardData;
    if (!data) return;
    const target = event.composedPath()[0];
    const text = data.types.includes("text/plain")
      ? data.getData("text/plain")
      : event.defaultPrevented
        ? ""
        : target instanceof HTMLTextAreaElement || target instanceof HTMLInputElement
          ? target.value.slice(target.selectionStart ?? 0, target.selectionEnd ?? 0)
          : (document.getSelection()?.toString() ?? "");
    const trimmed = text.trim();
    if (trimmed === text) return;
    data.setData("text/plain", trimmed);
    // Rich text and Monaco multicursor metadata can otherwise restore the untrimmed text on paste.
    data.clearData("text/html");
    data.clearData("vscode-editor-data");
    event.preventDefault();
  };
  document.addEventListener("copy", copy);
  return () => document.removeEventListener("copy", copy);
}
