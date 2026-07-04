// Terminal clipboard: copy/paste act on the focused terminal but read/write the OS clipboard through the
// host. The WebView's navigator.clipboard is focus- and permission-gated (it throws when the document isn't
// focused — the flakiness this fixes), so the host — which owns the real OS clipboard — does the work. Claude's
// OSC 52 ("set clipboard", its copy-on-highlight) flows through the same write path. On a browser-served shell
// (headless/remote) there is no host clipboard: copy writes via the browser's navigator.clipboard.writeText,
// but paste can't — a browser forbids navigator.clipboard.readText — so it declines and Ctrl+V falls through
// to the terminal's native paste event (see TerminalView), the only clipboard read a browser permits.

import type { Terminal } from "@xterm/xterm";
import { isBrowserHostedShell, onHostMessage, postToLocalHost } from "../bridge";
import { writeClipboard } from "../clipboard";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { base64ToBytes } from "./base64";
import { sendPastedImage } from "./paste-image";

// Live terminals by key (slot:pane) + the most recently focused one, so the copy/paste commands target the
// terminal the user is actually in (the keybindings are also gated `terminalFocused`).
const terminals = new Map<string, Terminal>();
let focusedKey: string | null = null;

/** Registers a terminal so the copy/paste commands can reach it; returns an unregister fn. */
export function registerTerminal(key: string, term: Terminal): () => void {
  terminals.set(key, term);
  return () => {
    if (terminals.get(key) === term) {
      terminals.delete(key);
    }
    if (focusedKey === key) {
      focusedKey = null;
    }
  };
}

/** Notes which terminal last took focus (its container received focusin). */
export function noteTerminalFocus(key: string): void {
  focusedKey = key;
}

function focusedTerminal(): Terminal | undefined {
  return focusedKey === null ? undefined : terminals.get(focusedKey);
}

let readSeq = 0;
const pendingReads = new Map<string, (text: string) => void>();
const pendingImageReads = new Map<string, (image: ClipboardImage) => void>();
// The host answers a clipboard read synchronously; the timeout only guards against a dropped bridge so a paste
// can't hang forever. It REJECTS (not resolves empty) so the paste handler can tell a dropped link from a
// genuinely-empty clipboard and surface the former.
const READ_TIMEOUT_MS = 3000;

// An image read from the OS clipboard: base64 bytes + MIME, or an empty `mime` when it holds no image.
interface ClipboardImage {
  mime: string;
  dataB64: string;
}

onHostMessage((message) => {
  if (message.type === "clipboard-content") {
    pendingReads.get(message.id)?.(message.text);
  } else if (message.type === "clipboard-image-content") {
    pendingImageReads.get(message.id)?.({ mime: message.mime, dataB64: message.dataB64 });
  }
});

// One request/reply round-trip to the LOCAL host, which owns the OS clipboard even when a remote backend drives
// the page (a remote/headless backend has none). Native WebView only — a browser tab declines to the DOM paste
// event and never gets here. The timeout only guards a dropped bridge; the host answers synchronously.
function requestFromLocalHost<T>(
  send: (id: string) => void,
  pending: Map<string, (value: T) => void>,
): Promise<T> {
  const id = `clip${++readSeq}`;
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      reject(new Error("the host didn't respond"));
    }, READ_TIMEOUT_MS);
    pending.set(id, (value) => {
      clearTimeout(timer);
      pending.delete(id);
      resolve(value);
    });
    send(id);
  });
}

const readClipboard = (): Promise<string> =>
  requestFromLocalHost((id) => postToLocalHost({ type: "clipboard-read", id }), pendingReads);

const readClipboardImage = (): Promise<ClipboardImage> =>
  requestFromLocalHost(
    (id) => postToLocalHost({ type: "clipboard-read-image", id }),
    pendingImageReads,
  );

// The native WebView consumes Ctrl/Cmd+V, so the DOM paste event never fires — read the OS clipboard through the
// host instead. On the claude pane (`key` is `slot:pane`) an image is shipped to the backend as a scratch file +
// injected path (a headless Claude has no clipboard of its own); everything else pastes as text.
async function pasteFromHost(term: Terminal, key: string): Promise<void> {
  try {
    const sep = key.lastIndexOf(":");
    if (key.slice(sep + 1) === "claude") {
      const image = await readClipboardImage();
      if (image.mime.length > 0) {
        sendPastedImage(key.slice(0, sep), image.mime, image.dataB64);
        return;
      }
    }
    const text = await readClipboard();
    if (text.length > 0) {
      term.paste(text);
    }
  } catch (error) {
    notify(
      "warn",
      `Couldn't paste from the clipboard: ${error instanceof Error ? error.message : String(error)}`,
    );
  }
}

/**
 * Routes the terminal's OSC 52 ("set clipboard" — Claude's copy-on-highlight) to the OS clipboard. A read
 * request (`52;c;?`) is denied: the child can't read the user's clipboard back. Returns the xterm disposable.
 */
export function attachOsc52(term: Terminal): { dispose(): void } {
  return term.parser.registerOscHandler(52, (data) => {
    const sep = data.indexOf(";");
    const payload = sep < 0 ? "" : data.slice(sep + 1);
    if (sep < 0 || payload === "?") {
      return true; // malformed, or a read request — consume without acting
    }
    try {
      writeClipboard(new TextDecoder().decode(base64ToBytes(payload)));
    } catch {
      // not valid base64; ignore (still consume the sequence)
    }
    return true;
  });
}

/** Registers the terminal copy/paste command handlers; returns a teardown fn. */
export function installTerminalClipboardCommands(): () => void {
  // Copy consumes the chord whenever a terminal is focused (so an empty-selection Ctrl+Shift+C can't fall
  // through to, say, the WebView's DevTools), copying only when there's a selection.
  const offCopy = registerCommand(CommandIds.terminalCopy, () => {
    const term = focusedTerminal();
    if (term === undefined) {
      return false;
    }
    writeClipboard(term.getSelection());
    return true;
  });

  const offPaste = registerCommand(CommandIds.terminalPaste, () => {
    const key = focusedKey;
    const term = focusedTerminal();
    if (term === undefined || key === null) {
      return false;
    }
    // A served browser tab forbids programmatic clipboard reads (navigator.clipboard.readText throws "not
    // allowed"), so decline: the keystroke falls through to the terminal's DOM paste event (see TerminalView),
    // which handles both text and — on the claude pane — images. Only the native WebView reads through the host.
    if (isBrowserHostedShell()) {
      return false;
    }
    return pasteFromHost(term, key);
  });

  const offClear = registerCommand(CommandIds.terminalClear, () => {
    const term = focusedTerminal();
    if (term === undefined) {
      return false;
    }
    term.clear();
    return true;
  });

  return () => {
    offCopy();
    offPaste();
    offClear();
  };
}
