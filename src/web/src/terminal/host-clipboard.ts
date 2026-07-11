// Terminal clipboard commands. Clipboard reads belong to the local machine even when the active session is remote.

import type { Terminal } from "@xterm/xterm";
import { activeBackendId, isBrowserHostedShell } from "../bridge";
import { writeClipboard } from "../clipboard";
import { readClipboardImage, readClipboardText } from "../clipboard-read";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { base64ToBytes } from "./base64";
import { sendPastedImage } from "./paste-image";

const terminals = new Map<string, Terminal>();
let focusedKey: string | null = null;

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

export function noteTerminalFocus(key: string): void {
  focusedKey = key;
}

function focusedTerminal(): Terminal | undefined {
  return focusedKey === null ? undefined : terminals.get(focusedKey);
}

async function pasteFromHost(term: Terminal, key: string, backendId: string): Promise<void> {
  try {
    const sep = key.lastIndexOf(":");
    if (key.slice(sep + 1) === "claude") {
      const image = await readClipboardImage();
      if (image.mime.length > 0) {
        sendPastedImage(backendId, key.slice(0, sep), image.mime, image.dataB64);
        return;
      }
    }
    const text = await readClipboardText();
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

export function attachOsc52(term: Terminal): { dispose(): void } {
  return term.parser.registerOscHandler(52, (data) => {
    const sep = data.indexOf(";");
    const payload = sep < 0 ? "" : data.slice(sep + 1);
    if (sep < 0 || payload === "?") {
      return true;
    }
    try {
      writeClipboard(new TextDecoder().decode(base64ToBytes(payload)));
    } catch {
      // Invalid OSC payloads are consumed without reaching the terminal child.
    }
    return true;
  });
}

export function installTerminalClipboardCommands(): () => void {
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
    const backendId = activeBackendId();
    return isBrowserHostedShell() ? false : pasteFromHost(term, key, backendId);
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
