// The web command registry: holds the host-injected catalog + keybindings, registers web handlers, and
// dispatches from keybindings, the palette, or a bound-view request. Core commands use the owning session's
// commands.invoke request. See docs/specs/commands.md.

import {
  beginClientSelectionCandidate,
  type ClientSession,
  hostInjected,
  invokeCommandOnBackend,
  LOCAL_BACKEND_ID,
  log,
  onSelectedSession,
  registerHostFeature,
  registerViewFeature,
  selectedSession,
  waitForClientSession,
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
export interface CommandContext {
  session: ClientSession | null;
}

export type CommandHandler = (
  args: unknown,
  context: CommandContext,
) => void | boolean | Promise<void>;

interface CommandCatalog {
  commands: CommandInfo[];
  keybindings: ResolvedKeybinding[];
}

/** The page-serving host's command catalog id. */
export const LOCAL_COMMAND_CATALOG_ID = LOCAL_BACKEND_ID;

const catalogs = new Map<string, CommandCatalog>([
  [
    LOCAL_BACKEND_ID,
    {
      commands: hostInjected("__WEAVIE_COMMANDS__", window.__WEAVIE_COMMANDS__, []),
      keybindings: hostInjected("__WEAVIE_KEYBINDINGS__", window.__WEAVIE_KEYBINDINGS__, []),
    },
  ],
]);
const handlers = new Map<string, CommandHandler>();
const changeSubscribers = new Set<() => void>();
const sessionActivationSubscribers = new Set<(activation: SessionActivation) => void>();

export interface SessionActivation {
  session: ClientSession;
  created: boolean;
}

function currentCatalog(): CommandCatalog {
  return catalogFor(getActiveCatalogBackendId());
}

function catalogFor(backendId: string): CommandCatalog {
  return catalogs.get(backendId) ?? { commands: [], keybindings: [] };
}

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
  return currentCatalog().commands;
}

/** The current resolved keybindings. */
export function getKeybindings(): ResolvedKeybinding[] {
  return currentCatalog().keybindings;
}

/** The backend whose command catalog currently drives session-scoped commands and shortcuts. */
export function getActiveCatalogBackendId(): string {
  return selectedSession()?.connection.id ?? LOCAL_BACKEND_ID;
}

/** One backend's commands, independent of the selected session. */
export function getCommandsInCatalog(backendId: string): CommandInfo[] {
  return catalogFor(backendId).commands;
}

/** One backend's resolved keybindings, independent of the selected session. */
export function getKeybindingsInCatalog(backendId: string): ResolvedKeybinding[] {
  return catalogFor(backendId).keybindings;
}

/** Looks up a command by id. */
export function findCommand(id: string): CommandInfo | undefined {
  return getCommands().find((command) => command.id === id);
}

/** Looks up a command in one backend's catalog, independent of the selected session. */
export function findCommandInCatalog(backendId: string, id: string): CommandInfo | undefined {
  return catalogFor(backendId).commands.find((command) => command.id === id);
}

// Run a Core command and return its result. A `backendId` arg (a rail / cloud-panel op on a specific session)
// targets that backend so the command runs on the session's owning host; otherwise the active backend.
export async function applySessionActivation(
  backendId: string,
  result: CommandResult,
  commit: ReturnType<typeof beginClientSelectionCandidate>,
): Promise<ClientSession | null> {
  const data = result.data as
    | {
        activateSession?: unknown;
        createdSession?: unknown;
        address?: { slot?: unknown; incarnation?: unknown };
      }
    | undefined;
  if (data?.activateSession !== true) {
    return null;
  }
  const address = data.address;
  if (
    address === undefined ||
    typeof address.slot !== "string" ||
    address.slot.length === 0 ||
    typeof address.incarnation !== "string" ||
    address.incarnation.length === 0
  ) {
    throw new Error("The command requested session activation without an exact live address.");
  }
  const session = await waitForClientSession(backendId, {
    slot: address.slot,
    incarnation: address.incarnation,
  });
  if (!commit(session)) {
    return null;
  }
  const activation = { session, created: data.createdSession === true };
  for (const handler of sessionActivationSubscribers) {
    handler(activation);
  }
  return session;
}

async function routeCoreCommand(
  id: string,
  args: unknown,
  catalogBackendId: string,
): Promise<CommandResult> {
  const fields = args as { backendId?: unknown; id?: unknown; classify?: unknown } | undefined;
  const backendId = fields?.backendId;
  const target =
    typeof backendId === "string" && backendId.length > 0 ? backendId : catalogBackendId;
  const commit = beginClientSelectionCandidate();
  const run = async (): Promise<CommandResult> => {
    const result = await invokeCommandOnBackend(target, id, args);
    if (result.ok) {
      await applySessionActivation(target, result, commit);
    }
    return result;
  };
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

/** Runs after an exact session activation wins the client-selection race. */
export function onSessionActivated(handler: (activation: SessionActivation) => void): () => void {
  sessionActivationSubscribers.add(handler);
  return () => sessionActivationSubscribers.delete(handler);
}

/**
 * Runs a command from a keybinding. Returns true when the command consumed the event (so the binding
 * should preventDefault), false when it declined or couldn't run (let the keystroke through).
 */
function runKeybindingFromCatalog(backendId: string, id: string, args: unknown): boolean {
  const command = findCommandInCatalog(backendId, id);
  if (command === undefined) {
    log("warn", `keybinding references unknown command '${id}'`);
    return false;
  }
  if (command.runsIn === "core") {
    // Keystrokes don't await the outcome; fire it (surfacing any error/informational message as a toast, so a
    // keyboard-run command isn't a silent no-op — e.g. Cycle Theme Mode reports the new mode when system and
    // the OS polarity render identically) and consume the key.
    void runCommandFromCatalogWithFeedback(backendId, id, args);
    return true;
  }
  const handler = handlers.get(id);
  if (handler === undefined) {
    log("warn", `no web handler registered for command '${id}'`);
    return false;
  }
  let outcome: ReturnType<CommandHandler>;
  try {
    outcome = handler(args, { session: selectedSession() });
  } catch (error) {
    // A thrown handler is a failure, not a decline — surface it (matching the palette) rather than swallow it
    // to the console, so a keyboard-run command isn't a silent no-op. It still consumed the key.
    notify("warn", String(error));
    return true;
  }
  // The sync return can't await a rejecting async handler; surface its rejection the same way.
  if (outcome instanceof Promise) {
    void outcome.catch((error: unknown) => notify("warn", String(error)));
    return true;
  }
  // Only an explicit `false` declines; undefined consumes the key.
  return outcome !== false;
}

export function runForKeybinding(id: string, args: unknown): boolean {
  return runKeybindingFromCatalog(getActiveCatalogBackendId(), id, args);
}

/** Runs a keybinding against its owning catalog, independent of the selected session. */
export function runForKeybindingFromCatalog(backendId: string, id: string, args: unknown): boolean {
  return runKeybindingFromCatalog(backendId, id, args);
}

/**
 * Runs a command from the palette / a menu / programmatically and resolves to its result, so callers that care
 * (e.g. a toast) can react. A Core command round-trips to its backend; a web command runs locally and its
 * return maps onto the result (an explicit `false` ⇒ declined). Never rejects — failures resolve as `ok: false`.
 */
function dispatchFromCatalog(backendId: string, id: string, args: unknown): Promise<CommandResult> {
  const command = findCommandInCatalog(backendId, id);
  if (command === undefined) {
    log("warn", `unknown command '${id}'`);
    return Promise.resolve({ ok: false, error: `Unknown command '${id}'.` });
  }
  if (command.runsIn === "core") {
    return routeCoreCommand(id, args, backendId);
  }
  const handler = handlers.get(id);
  if (handler === undefined) {
    log("warn", `no web handler registered for command '${id}'`);
    return Promise.resolve({ ok: false, error: `No web handler for '${id}'.` });
  }
  try {
    return Promise.resolve(handler(args, { session: selectedSession() }))
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

export function dispatchCommand(id: string, args?: unknown): Promise<CommandResult> {
  return dispatchFromCatalog(getActiveCatalogBackendId(), id, args);
}

/** Dispatches using one backend's catalog even when a session on another backend is selected. */
export function dispatchCommandFromCatalog(
  backendId: string,
  id: string,
  args?: unknown,
): Promise<CommandResult> {
  return dispatchFromCatalog(backendId, id, args);
}

/**
 * Dispatches a command and surfaces its outcome as a toast, so every menu/palette caller gives the same
 * feedback: a failure shows its `error`, an informational `message` shows as info. A bare success is silent —
 * the action's own effect (a chip changing, a pane opening) is the feedback.
 */
export async function runCommandWithFeedback(id: string, args?: unknown): Promise<CommandResult> {
  const result = await dispatchCommand(id, args);
  reportCommandResult(result);
  return result;
}

/** Dispatches from one backend's catalog and surfaces its outcome. */
export async function runCommandFromCatalogWithFeedback(
  backendId: string,
  id: string,
  args?: unknown,
): Promise<CommandResult> {
  const result = await dispatchCommandFromCatalog(backendId, id, args);
  reportCommandResult(result);
  return result;
}

function reportCommandResult(result: CommandResult): void {
  // A Core command round-trips over JSON, so an absent message/error arrives as `null`, not `undefined` —
  // compare with `!= null` so a silent success (e.g. a normal font zoom) doesn't fire an empty toast.
  if (!result.ok && result.error != null) {
    notify("warn", result.error);
  } else if (result.ok && result.message != null) {
    notify("info", result.message);
  }
}

function applyCatalog(backendId: string, catalog: CommandCatalog): void {
  catalogs.set(backendId, catalog);
  announceCommandsChanged();
}

function announceCommandsChanged(): void {
  for (const handler of changeSubscribers) {
    handler();
  }
}

onSelectedSession(announceCommandsChanged);

registerHostFeature((connection) => {
  const offHello = connection.onHello((hello) => applyCatalog(connection.id, hello.commandCatalog));
  const offCatalog = connection.host
    .feature("commands")
    .on<CommandCatalog>("catalog", (catalog) => applyCatalog(connection.id, catalog));
  return () => {
    offHello();
    offCatalog();
    catalogs.delete(connection.id);
  };
});

async function runBoundWebCommand(
  session: ClientSession,
  id: string,
  args: unknown,
): Promise<CommandResult> {
  const handler = handlers.get(id);
  if (handler === undefined) {
    return { ok: false, error: `No web handler for '${id}'.` };
  }
  try {
    const outcome = await handler(args, { session });
    return outcome === false
      ? { ok: false, error: `Command '${id}' declined the request.` }
      : { ok: true };
  } catch (error) {
    return { ok: false, error: String(error) };
  }
}

registerViewFeature((session) =>
  session
    .feature("commands")
    .handle<{ id: string; args: unknown }, CommandResult>("run", ({ id, args }) =>
      runBoundWebCommand(session, id, args),
    ),
);
