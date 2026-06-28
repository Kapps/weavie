// The web command registry: holds the host-injected catalog + keybindings, registers web handlers, and
// dispatches (from keybindings, the palette, or the host's run-command). Core commands forward to the host
// as invoke-command. See docs/specs/commands.md.

import {
  activeBackendId,
  hostInjected,
  invokeCommandOnBackend,
  log,
  onHostMessage,
  postToHost,
} from "../bridge";
import { trackSessionCommand } from "../chrome/session-store";
import { notify } from "../notify/notify";
import { CommandIds, type CommandInfo, type CommandResult, type ResolvedKeybinding } from "./types";

// Session-lifecycle commands the user waits on the session to answer: while one is in flight, the session's
// chip shows a spinner (session-store's pending set). The delete's classify probe is excluded — it's a quick
// read with no mutation, so it shouldn't flash a spinner.
const SESSION_LIFECYCLE = new Set<string>([
  CommandIds.loadSession,
  CommandIds.unloadSession,
  CommandIds.deleteSession,
]);

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

// Run a Core command and return its result. A `backendId` arg (a rail / cloud-panel op on a specific session)
// targets that backend so the command runs on the session's owning host; otherwise the active backend.
function routeCoreCommand(id: string, args: unknown): Promise<CommandResult> {
  const fields = args as { backendId?: unknown; id?: unknown; classify?: unknown } | undefined;
  const backendId = fields?.backendId;
  const target =
    typeof backendId === "string" && backendId.length > 0 ? backendId : activeBackendId();
  const run = (): Promise<CommandResult> => invokeCommandOnBackend(target, id, args);
  // A session-lifecycle op (not the delete's classify probe) flags its session as pending until it settles.
  if (SESSION_LIFECYCLE.has(id) && typeof fields?.id === "string" && fields.classify !== true) {
    return trackSessionCommand(target, fields.id, run);
  }
  return run();
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
    // Keystrokes don't await the outcome; fire it (surfacing any error/informational message as a toast, so a
    // keyboard-run command isn't a silent no-op — e.g. Cycle Theme Mode reports the new mode when system and
    // the OS polarity render identically) and consume the key.
    void runCommandWithFeedback(id, args);
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

/**
 * Runs a command from the palette / a menu / programmatically and resolves to its result, so callers that care
 * (e.g. a toast) can react. A Core command round-trips to its backend; a web command runs locally and its
 * return maps onto the result (an explicit `false` ⇒ declined). Never rejects — failures resolve as `ok: false`.
 */
export function dispatchCommand(id: string, args?: unknown): Promise<CommandResult> {
  const command = findCommand(id);
  if (command === undefined) {
    log("warn", `unknown command '${id}'`);
    return Promise.resolve({ ok: false, error: `Unknown command '${id}'.` });
  }
  if (command.runsIn === "core") {
    return routeCoreCommand(id, args);
  }
  const handler = handlers.get(id);
  if (handler === undefined) {
    log("warn", `no web handler registered for command '${id}'`);
    return Promise.resolve({ ok: false, error: `No web handler for '${id}'.` });
  }
  try {
    return Promise.resolve(handler(args))
      .then((value) => ({ ok: value !== false }))
      .catch((error: unknown) => {
        log("error", `command '${id}' failed: ${String(error)}`);
        return { ok: false, error: String(error) };
      });
  } catch (error) {
    log("error", `command '${id}' threw: ${String(error)}`);
    return Promise.resolve({ ok: false, error: String(error) });
  }
}

/**
 * Dispatches a command and surfaces its outcome as a toast, so every menu/palette caller gives the same
 * feedback: a failure shows its `error`, an informational `message` shows as info. A bare success is silent —
 * the action's own effect (a chip changing, a pane opening) is the feedback.
 */
export async function runCommandWithFeedback(id: string, args?: unknown): Promise<CommandResult> {
  const result = await dispatchCommand(id, args);
  if (!result.ok && result.error !== undefined) {
    notify("error", result.error);
  } else if (result.ok && result.message !== undefined) {
    notify("info", result.message);
  }
  return result;
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

// Tokens currently being run, so a replayed run-command frame (the multi-connection headless bridge can
// re-deliver) doesn't run a non-idempotent web command twice. Bounded: a token is removed when its run settles.
const inFlightCommands = new Set<string>();

async function ackRunCommand(id: string, args: unknown, token: string): Promise<void> {
  if (inFlightCommands.has(token)) {
    return; // a replayed delivery of an invocation already in flight
  }
  const handler = handlers.get(id);
  if (handler === undefined) {
    postToHost({ type: "command-ack", token, ok: false, error: `no web handler for '${id}'` });
    return;
  }
  inFlightCommands.add(token);
  try {
    await handler(args);
    postToHost({ type: "command-ack", token, ok: true });
  } catch (error) {
    postToHost({ type: "command-ack", token, ok: false, error: String(error) });
  } finally {
    inFlightCommands.delete(token);
  }
}
