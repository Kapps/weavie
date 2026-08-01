// Terminal clipboard commands. Clipboard reads belong to the local machine even when the selected session is remote.

import type { Terminal } from "@xterm/xterm";
import { type ClientSession, isBrowserHostedShell, type TermSession } from "../bridge";
import { writeClipboard } from "../clipboard";
import { readClipboardImage, readClipboardText } from "../clipboard-read";
import { registerCommand } from "../commands/registry";
import { CommandIds } from "../commands/types";
import { notify } from "../notify/notify";
import { base64ToBytes } from "./base64";
import { sendPastedImage } from "./paste-image";

interface RegisteredTerminal {
  term: Terminal;
  session: ClientSession;
  pane: TermSession;
}

const terminals = new Map<string, RegisteredTerminal>();
let focusedKey: string | null = null;

export function registerTerminal(
  key: string,
  term: Terminal,
  session: ClientSession,
  pane: TermSession,
): () => void {
  const registered = { term, session, pane };
  terminals.set(key, registered);
  return () => {
    if (terminals.get(key) === registered) {
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

function focusedTerminal(): RegisteredTerminal | undefined {
  return focusedKey === null ? undefined : terminals.get(focusedKey);
}

async function pasteFromHost(target: RegisteredTerminal): Promise<void> {
  try {
    if (target.pane === "claude") {
      const image = await readClipboardImage();
      if (image.mime.length > 0) {
        sendPastedImage(target.session, image.mime, image.dataB64);
        return;
      }
    }
    const text = await readClipboardText();
    if (text.length > 0) {
      target.term.paste(text);
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
    const target = focusedTerminal();
    if (target === undefined) {
      return false;
    }
    writeClipboard(target.term.getSelection());
    return true;
  });

  const offPaste = registerCommand(CommandIds.terminalPaste, () => {
    const target = focusedTerminal();
    if (target === undefined) {
      return false;
    }
    return isBrowserHostedShell() ? false : pasteFromHost(target);
  });

  const offClear = registerCommand(CommandIds.terminalClear, () => {
    const target = focusedTerminal();
    if (target === undefined) {
      return false;
    }
    target.term.clear();
    return true;
  });

  return () => {
    offCopy();
    offPaste();
    offClear();
  };
}
