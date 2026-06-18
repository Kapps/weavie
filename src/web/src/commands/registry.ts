// The web command registry: holds the catalog + resolved keybindings the host injected, lets web features
// register handlers for their ids, and dispatches. Three triggers land here — keybindings (keybindings.ts),
// the omnibar command palette (Omnibar.tsx), and the host's run-command (a web command Claude invoked over
// MCP, which we run + ack). Core commands are forwarded to the host as invoke-command. See docs/specs/commands.md.

import { log, onHostMessage, postToHost } from "../bridge";
import type { CommandInfo, ResolvedKeybinding } from "./types";

// A web command handler. Return `false` to decline (let a keybinding's keystroke fall through to the
// editor/terminal); anything else — including a Promise or undefined — consumes the event.
export type CommandHandler = (args: unknown) => void | boolean | Promise<void>;

let commands: CommandInfo[] = window.__WEAVIE_COMMANDS__ ?? [];
let keybindings: ResolvedKeybinding[] = window.__WEAVIE_KEYBINDINGS__ ?? [];
const handlers = new Map<string, CommandHandler>();
const changeSubscribers = new Set<() => void>();

/** Registers the handler for a web command id; returns an unregister function. */
export function registerCommand(id: string, handler: CommandHandler): () => void {
  handlers.set(id, handler);
  return () => {
    if (handlers.get(id) === handler) {
      handlers.delete(id);
    }
  };
}

/** The current command catalog. */
export function getCommands(): CommandInfo[] {
  return commands;
}

/** The current resolved keybindings. */
export function getKeybindings(): ResolvedKeybinding[] {
  return keybindings;
}

/** Looks up a command by id. */
export function findCommand(id: string): CommandInfo | undefined {
  return commands.find((c) => c.id === id);
}

/** Subscribe to catalog/keybinding changes (the host pushed an update); returns an unsubscribe function. */
export function onCommandsChanged(handler: () => void): () => void {
  changeSubscribers.add(handler);
  return () => changeSubscribers.delete(handler);
}

/**
 * Runs a command from a keybinding. Returns true when the command consumed the event (so the binding
 * should preventDefault), false when it declined or couldn't run (let the keystroke through).
 */
export function runForKeybinding(id: string, args: unknown): boolean {
  const command = findCommand(id);
  if (command === undefined) {
    log("warn", `keybinding references unknown command '${id}'`);
    return false;
  }
  if (command.runsIn === "core") {
    postToHost({ type: "invoke-command", id, args });
    return true;
  }
  const handler = handlers.get(id);
  if (handler === undefined) {
    log("warn", `no web handler registered for command '${id}'`);
    return false;
  }
  try {
    // Only an explicit `false` declines; a Promise/undefined consumed the key.
    return handler(args) !== false;
  } catch (error) {
    log("error", `command '${id}' threw: ${String(error)}`);
    return true;
  }
}

/** Runs a command from the palette / programmatically (return value ignored; errors are logged). */
export function dispatchCommand(id: string, args?: unknown): void {
  const command = findCommand(id);
  if (command === undefined) {
    log("warn", `unknown command '${id}'`);
    return;
  }
  if (command.runsIn === "core") {
    postToHost({ type: "invoke-command", id, args });
    return;
  }
  const handler = handlers.get(id);
  if (handler === undefined) {
    log("warn", `no web handler registered for command '${id}'`);
    return;
  }
  try {
    void Promise.resolve(handler(args)).catch((error: unknown) =>
      log("error", `command '${id}' failed: ${String(error)}`),
    );
  } catch (error) {
    log("error", `command '${id}' threw: ${String(error)}`);
  }
}

// Host → web: catalog/keybinding push (live keybindings.json edit) + run-command (a web command Claude
// invoked over MCP). For run-command we run the local handler and ack the outcome honestly.
onHostMessage((message) => {
  if (message.type === "commands") {
    commands = message.commands;
    keybindings = message.keybindings;
    for (const handler of changeSubscribers) {
      handler();
    }
  } else if (message.type === "run-command") {
    void ackRunCommand(message.id, message.args, message.token);
  }
});

async function ackRunCommand(id: string, args: unknown, token: string): Promise<void> {
  const handler = handlers.get(id);
  if (handler === undefined) {
    postToHost({ type: "command-ack", token, ok: false, error: `no web handler for '${id}'` });
    return;
  }
  try {
    await handler(args);
    postToHost({ type: "command-ack", token, ok: true });
  } catch (error) {
    postToHost({ type: "command-ack", token, ok: false, error: String(error) });
  }
}
