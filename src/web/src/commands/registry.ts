// The web command registry: holds the host-injected catalog + keybindings, registers web handlers, and
// dispatches (from keybindings, the palette, or the host's run-command). Core commands forward to the host
// as invoke-command. See docs/specs/commands.md.

import { hostInjected, log, onHostMessage, postToBackend, postToHost } from "../bridge";
import type { CommandInfo, ResolvedKeybinding } from "./types";

// A web command handler. Return `false` to decline (let a keybinding's keystroke fall through);
// anything else, including a Promise or undefined, consumes the event.
export type CommandHandler = (args: unknown) => void | boolean | Promise<void>;

let commands: CommandInfo[] = hostInjected("__WEAVIE_COMMANDS__", window.__WEAVIE_COMMANDS__, []);
let keybindings: ResolvedKeybinding[] = hostInjected(
  "__WEAVIE_KEYBINDINGS__",
  window.__WEAVIE_KEYBINDINGS__,
  [],
);
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

// Send a Core command to the host. A `backendId` arg (a rail / cloud-panel op on a specific session) targets
// that backend so the command runs on the session's owning host; otherwise it goes to the active backend.
function routeCoreCommand(id: string, args: unknown): void {
  const backendId = (args as { backendId?: unknown } | undefined)?.backendId;
  if (typeof backendId === "string" && backendId.length > 0) {
    postToBackend(backendId, { type: "invoke-command", id, args });
  } else {
    postToHost({ type: "invoke-command", id, args });
  }
}

/** Subscribe to catalog/keybinding changes; returns an unsubscribe function. */
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
    routeCoreCommand(id, args);
    return true;
  }
  const handler = handlers.get(id);
  if (handler === undefined) {
    log("warn", `no web handler registered for command '${id}'`);
    return false;
  }
  try {
    // Only an explicit `false` declines; a Promise/undefined consumes the key.
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
    routeCoreCommand(id, args);
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
// invoked over MCP). For run-command we run the local handler and ack the outcome.
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
