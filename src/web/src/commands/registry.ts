// The web command registry: holds the host-injected catalog + keybindings, registers web handlers, and
// dispatches from keybindings, the palette, or a bound-view request. Core commands use the owning session's
// commands.invoke request. See docs/specs/commands.md.

import {
  beginClientSelectionCandidate,
  type ClientSession,
  hostInjected,
  invokeClientCommandOnHost,
  invokeCommandOnBackend,
  invokeSessionCommandOnBackend,
  LOCAL_BACKEND_ID,
  log,
  onSelectedSession,
  registerHostFeature,
  registerSessionFeature,
  registerViewFeature,
  selectedSession,
  waitForClientSession,
} from "../bridge";
import { trackSessionCommand } from "../chrome/session-store";
import { requireSessionAddress } from "../messaging/message-envelope";
import { notify } from "../notify/notify";
import { CommandIds, type CommandInfo, type CommandResult, type ResolvedKeybinding } from "./types";

// Session-lifecycle commands the user waits on the session to answer: while one is in flight, the session's
// chip shows a spinner (session-store's pending set). The delete's preview is excluded — it's a quick
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
const executionLanes = new Map<string, Promise<void>>();
const changeSubscribers = new Set<() => void>();
const sessionActivationSubscribers = new Set<(activation: SessionActivation) => void>();
const terminalActivationSubscribers = new Set<(activation: TerminalActivation) => void>();

export interface SessionActivation {
  session: ClientSession;
  created: boolean;
}

export interface TerminalActivation {
  session: ClientSession;
  terminalId: string;
}

async function activationSession(
  backendId: string,
  data: { address?: unknown },
  error: string,
): Promise<ClientSession> {
  return waitForClientSession(backendId, requireSessionAddress(data.address, error));
}

function currentCatalog(): CommandCatalog {
  const backendId = getActiveCatalogBackendId();
  return {
    commands: commandsForClient(backendId),
    keybindings: keybindingsForClient(backendId),
  };
}

function catalogFor(backendId: string): CommandCatalog {
  return catalogs.get(backendId) ?? { commands: [], keybindings: [] };
}

const isClientOwned = (command: CommandInfo): boolean => command.owner === "client";

function commandsForClient(backendId: string): CommandInfo[] {
  const active = catalogFor(backendId).commands;
  if (backendId === LOCAL_BACKEND_ID) {
    return active;
  }
  const local = catalogFor(LOCAL_BACKEND_ID).commands.filter(isClientOwned);
  const localById = new Map(local.map((command) => [command.id, command]));
  const merged = active.flatMap((command) => {
    const client = localById.get(command.id);
    if (client !== undefined) {
      localById.delete(command.id);
      return [client];
    }
    return isClientOwned(command) ? [] : [command];
  });
  return [...merged, ...localById.values()];
}

function keybindingsForClient(backendId: string): ResolvedKeybinding[] {
  return keybindingEntriesForClient(backendId).map(({ binding }) => binding);
}

export interface CatalogKeybinding {
  catalogBackendId: string;
  binding: ResolvedKeybinding;
}

function keybindingEntriesForClient(backendId: string): CatalogKeybinding[] {
  const active = catalogFor(backendId);
  if (backendId === LOCAL_BACKEND_ID) {
    return active.keybindings.map((binding) => ({ catalogBackendId: backendId, binding }));
  }
  const local = catalogFor(LOCAL_BACKEND_ID);
  const localClientIds = new Set(local.commands.filter(isClientOwned).map((command) => command.id));
  const remoteClientIds = new Set(
    active.commands.filter(isClientOwned).map((command) => command.id),
  );
  return [
    ...active.keybindings
      .filter(
        (binding) => !localClientIds.has(binding.command) && !remoteClientIds.has(binding.command),
      )
      .map((binding) => ({ catalogBackendId: backendId, binding })),
    ...local.keybindings
      .filter((binding) => localClientIds.has(binding.command))
      .map((binding) => ({ catalogBackendId: LOCAL_BACKEND_ID, binding })),
  ];
}

function commandForClient(backendId: string, id: string): CommandInfo | undefined {
  return commandsForClient(backendId).find((command) => command.id === id);
}

function executionLaneKey(
  command: CommandInfo,
  catalogBackendId: string,
  session: ClientSession | null,
): string {
  if (command.owner === "client") {
    return `${LOCAL_BACKEND_ID}\0${command.executionLane}`;
  }
  return session === null
    ? `${catalogBackendId}\0${command.executionLane}`
    : `${session.connection.id}\0${session.address.slot}\0${session.address.incarnation}\0${command.executionLane}`;
}

function trackExecutionLane<T>(lane: string, result: Promise<T>): Promise<T> {
  const tail = result.then(
    () => undefined,
    () => undefined,
  );
  executionLanes.set(lane, tail);
  void tail.finally(() => {
    if (executionLanes.get(lane) === tail) {
      executionLanes.delete(lane);
    }
  });
  return result;
}

function runInExecutionLane<T>(lane: string, run: () => Promise<T>): Promise<T> {
  const prior = executionLanes.get(lane) ?? Promise.resolve();
  return trackExecutionLane(lane, prior.catch(() => undefined).then(run));
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

/** The active resolved bindings paired with the catalog that must dispatch each command. */
export function getActiveKeybindingEntries(): CatalogKeybinding[] {
  return keybindingEntriesForClient(getActiveCatalogBackendId());
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
  return commandForClient(getActiveCatalogBackendId(), id);
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
  const session = await activationSession(
    backendId,
    data,
    "The command requested session activation without an exact live address.",
  );
  if (!commit(session)) {
    return null;
  }
  const activation = { session, created: data.createdSession === true };
  for (const handler of sessionActivationSubscribers) {
    handler(activation);
  }
  return session;
}

export async function applyTerminalActivation(
  backendId: string,
  result: CommandResult,
): Promise<ClientSession | null> {
  const data = result.data as
    | {
        activateTerminal?: unknown;
        terminalId?: unknown;
        address?: { slot?: unknown; incarnation?: unknown };
      }
    | undefined;
  if (data?.activateTerminal !== true) {
    return null;
  }
  if (typeof data.terminalId !== "string" || data.terminalId.length === 0) {
    throw new Error("The command requested terminal activation without an exact terminal id.");
  }
  const session = await activationSession(
    backendId,
    data,
    "The command requested terminal activation without an exact live session address.",
  );
  const activation = { session, terminalId: data.terminalId };
  for (const handler of terminalActivationSubscribers) {
    handler(activation);
  }
  return session;
}

async function routeCoreCommand(
  command: CommandInfo,
  args: unknown,
  catalogBackendId: string,
): Promise<CommandResult> {
  const fields = args as { backendId?: unknown; id?: unknown; operation?: unknown } | undefined;
  const backendId = fields?.backendId;
  const target =
    command.owner === "client"
      ? LOCAL_BACKEND_ID
      : typeof backendId === "string" && backendId.length > 0
        ? backendId
        : catalogBackendId;
  const active = selectedSession();
  const selectedId =
    SESSION_LIFECYCLE.has(command.id) &&
    typeof fields?.id !== "string" &&
    active?.connection.id === target
      ? active.address.slot
      : undefined;
  const routedArgs = selectedId === undefined ? args : { ...(fields ?? {}), id: selectedId };
  const trackedId = typeof fields?.id === "string" ? fields.id : selectedId;
  const commit = beginClientSelectionCandidate();
  const run = async (): Promise<CommandResult> => {
    const result = await (command.owner === "client"
      ? invokeClientCommandOnHost(command.id, routedArgs)
      : command.scope === "host"
        ? invokeSessionCommandOnBackend(target, command.id, routedArgs)
        : invokeCommandOnBackend(target, command.id, routedArgs));
    if (result.ok) {
      await applySessionActivation(target, result, commit);
      await applyTerminalActivation(target, result);
    }
    return result;
  };
  // A mutating session-lifecycle op (not the delete preview) flags its session as pending until it settles.
  const deletePreview = command.id === CommandIds.deleteSession && fields?.operation === "preview";
  if (SESSION_LIFECYCLE.has(command.id) && trackedId !== undefined && !deletePreview) {
    return trackSessionCommand(target, trackedId, run);
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

/** Runs when a Core command asks the page to activate an exact terminal tab. */
export function onTerminalActivated(handler: (activation: TerminalActivation) => void): () => void {
  terminalActivationSubscribers.add(handler);
  return () => terminalActivationSubscribers.delete(handler);
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
  const session = selectedSession();
  const lane = executionLaneKey(command, backendId, session);
  if (executionLanes.has(lane)) {
    void runInExecutionLane(lane, async () => handler(args, { session })).catch((error: unknown) =>
      notify("warn", String(error)),
    );
    return true;
  }

  let outcome: ReturnType<CommandHandler>;
  try {
    outcome = handler(args, { session });
  } catch (error) {
    // A thrown handler is a failure, not a decline — surface it (matching the palette) rather than swallow it
    // to the console, so a keyboard-run command isn't a silent no-op. It still consumed the key.
    notify("warn", String(error));
    return true;
  }
  // The sync return can't await a rejecting async handler; surface its rejection the same way.
  if (outcome instanceof Promise) {
    void trackExecutionLane(lane, outcome).catch((error: unknown) => notify("warn", String(error)));
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
  const command = commandForClient(backendId, id);
  if (command === undefined) {
    log("warn", `unknown command '${id}'`);
    return Promise.resolve({ ok: false, error: `Unknown command '${id}'.` });
  }
  if (command.runsIn === "core") {
    return routeCoreCommand(command, args, backendId);
  }
  const handler = handlers.get(id);
  if (handler === undefined) {
    log("warn", `no web handler registered for command '${id}'`);
    return Promise.resolve({ ok: false, error: `No web handler for '${id}'.` });
  }
  const session = selectedSession();
  return runInExecutionLane(executionLaneKey(command, backendId, session), async () => {
    try {
      const value = await handler(args, { session });
      return { ok: value !== false };
    } catch (error) {
      log("error", `command '${id}' failed: ${String(error)}`);
      return { ok: false, error: String(error) };
    }
  });
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
  const command = findCommandInCatalog(session.connection.id, id);
  if (command?.runsIn !== "web") {
    return { ok: false, error: `No web command '${id}' exists in this session's catalog.` };
  }
  const handler = handlers.get(id);
  if (handler === undefined) {
    return { ok: false, error: `No web handler for '${id}'.` };
  }
  return runInExecutionLane(executionLaneKey(command, session.connection.id, session), async () => {
    try {
      const outcome = await handler(args, { session });
      return outcome === false
        ? { ok: false, error: `Command '${id}' declined the request.` }
        : { ok: true };
    } catch (error) {
      return { ok: false, error: String(error) };
    }
  });
}

registerViewFeature((session) =>
  session
    .feature("commands")
    .handleConcurrent<{ id: string; args: unknown }, CommandResult>("run", ({ id, args }) =>
      runBoundWebCommand(session, id, args),
    ),
);

registerSessionFeature((session) =>
  session
    .feature("commands")
    .handleConcurrent<{ id: string; args: unknown }, CommandResult>("runClient", ({ id, args }) => {
      const command = findCommandInCatalog(LOCAL_BACKEND_ID, id);
      if (command?.owner !== "client") {
        return Promise.resolve({
          ok: false,
          error: `Command '${id}' is not owned by the local presentation client.`,
        });
      }
      return dispatchFromCatalog(LOCAL_BACKEND_ID, id, args);
    }),
);
