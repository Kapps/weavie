// Terminal clipboard: copy/paste act on the focused terminal but read/write the OS clipboard through the
// host. The WebView's navigator.clipboard is focus- and permission-gated (it throws when the document isn't
// focused — the flakiness this fixes), so the host — which owns the real OS clipboard — does the work. Claude's
// OSC 52 ("set clipboard", its copy-on-highlight) flows through the same write path.

import type { Terminal } from "@xterm/xterm";
import { onHostMessage, postToHost } from "../bridge";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { base64ToBytes } from "./base64";

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

function writeClipboard(text: string): void {
  if (text.length > 0) {
    postToHost({ type: "clipboard-write", text });
  }
}

let readSeq = 0;
const pendingReads = new Map<string, (text: string) => void>();
// The host answers clipboard-read synchronously; the timeout only guards against a dropped bridge so a paste
// can't hang forever. It REJECTS (not resolves "") so the paste handler can tell a dropped link from a
// genuinely-empty clipboard and surface the former.
const READ_TIMEOUT_MS = 3000;

onHostMessage((message) => {
  if (message.type === "clipboard-content") {
    pendingReads.get(message.id)?.(message.text);
  }
});

function readClipboard(): Promise<string> {
  const id = `clip${++readSeq}`;
  return new Promise<string>((resolve, reject) => {
    const timer = setTimeout(() => {
      pendingReads.delete(id);
      reject(new Error("the host didn't respond"));
    }, READ_TIMEOUT_MS);
    pendingReads.set(id, (text) => {
      clearTimeout(timer);
      pendingReads.delete(id);
      resolve(text);
    });
    postToHost({ type: "clipboard-read", id });
  });
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
    const term = focusedTerminal();
    if (term === undefined) {
      return false;
    }
    return readClipboard().then(
      (text) => {
        if (text.length > 0) {
          term.paste(text);
        }
      },
      (error: unknown) =>
        notify(
          "warn",
          `Couldn't paste from the clipboard: ${error instanceof Error ? error.message : String(error)}`,
        ),
    );
  });

  return () => {
    offCopy();
    offPaste();
  };
}
