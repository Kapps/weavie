// The WebView half of LSP: monaco-languageclient connected to the host's language servers over the Weavie
// bridge (no socket of its own — see lsp-bridge-transport.ts), via injected `window.__WEAVIE_LSP__`. A client
// starts lazily — only when a document of its language first opens — so csharp-ls/gopls aren't spawned until a
// .cs/.go file appears.
//
// Each session has its OWN worktree root + slot. A session switch pushes the incoming config and
// `rebindLanguageServices` tears every client down and reconnects, so intelligence follows the worktree.

import * as monaco from "monaco-editor";
import { MonacoLanguageClient } from "monaco-languageclient";
import { CloseAction, ErrorAction } from "vscode-languageclient";
import { log } from "../bridge";
import { initEditorServices } from "../editor/vscode-services";
import { notify } from "../notify/notify";
import { openLspChannel } from "./lsp-bridge-transport";

/** One language server the host offers: bridge selector id, the languages it serves, and its defaults. */
export interface WeavieLspServer {
  id: string;
  languageIds: string[];
  settings: Record<string, unknown> | null;
}

/** LSP discovery the C# host injects: the session's slot (frames are tagged with it), worktree root, + catalog. */
export interface WeavieLspConfig {
  slot: string;
  workspace: string;
  servers: WeavieLspServer[];
}

declare global {
  interface Window {
    /** LSP bridge config injected by the host before navigation; absent in a plain-browser dev run. */
    __WEAVIE_LSP__?: WeavieLspConfig;
  }
}

// Server ids with a live (or connecting) client, so we don't double-start one.
const started = new Set<string>();
// Server id → teardown (dispose the client + close its bridge channel). A session switch tears every client down.
const clients = new Map<string, () => void>();
// The active session's config, read live (not captured) so lazy starts after a switch use the new session's
// slot/root. Undefined until the host injects/pushes one.
let activeConfig: WeavieLspConfig | undefined;
let serverByLanguage = new Map<string, WeavieLspServer>();
let modelHooksInstalled = false;
// Bumped on every rebind; a connect() captures its generation and stands down if a switch superseded it.
let generation = 0;
// Monotonic per-page channel id, so even a fast stop/start on one server can't confuse stale frames.
let channelSeq = 0;

// Listeners notified when a language client finishes starting (and on rebind), so consumers that query LSP
// providers — the test-lens provider — can refresh once a server is actually able to answer.
const languageClientStartedListeners = new Set<() => void>();

/** The active session's worktree root (from the LSP config), or undefined before the host injects one. */
export function currentWorkspaceRoot(): string | undefined {
  return activeConfig?.workspace;
}

/** Subscribes to language-client-started events (fired when a client starts or services rebind). Returns an unsubscribe. */
export function onLanguageClientStarted(listener: () => void): () => void {
  languageClientStartedListeners.add(listener);
  return () => {
    languageClientStartedListeners.delete(listener);
  };
}

function notifyLanguageClientStarted(): void {
  for (const listener of languageClientStartedListeners) {
    listener();
  }
}

function indexServers(config: WeavieLspConfig): void {
  serverByLanguage = new Map();
  for (const server of config.servers) {
    for (const languageId of server.languageIds) {
      serverByLanguage.set(languageId, server);
    }
  }
}

function maybeStart(languageId: string): void {
  const config = activeConfig;
  if (config === undefined) {
    return;
  }
  const server = serverByLanguage.get(languageId);
  if (server !== undefined && !started.has(server.id)) {
    started.add(server.id);
    connect(config, server);
  }
}

function startForOpenModels(): void {
  for (const model of monaco.editor.getModels()) {
    maybeStart(model.getLanguageId());
  }
}

/**
 * Wires lazy, per-language LSP for the session loaded at launch. No-op without injected bridge config
 * (plain-browser dev) — the editor still works, just without language intelligence.
 */
export async function startLanguageServices(): Promise<void> {
  const config = window.__WEAVIE_LSP__;
  if (config === undefined) {
    return;
  }
  // The editor's VSCode services must be initialized (with our overrides) before any monaco.editor.* call below —
  // touching monaco auto-initializes the standalone services, which then makes the editor host's initialize()
  // throw "Services are already initialized". initEditorServices() is idempotent, so this just enforces order.
  await initEditorServices();
  activeConfig = config;
  indexServers(config);
  if (!modelHooksInstalled) {
    modelHooksInstalled = true;
    monaco.editor.onDidCreateModel((model) => {
      maybeStart(model.getLanguageId());
      model.onDidChangeLanguage((e) => maybeStart(e.newLanguage));
    });
  }
  startForOpenModels();
}

/**
 * Rebind language services to a different session's bridge on a session switch: tear every (previous-bridge)
 * client down and reconnect against the incoming session's bridge, lazily for the open documents.
 */
export async function rebindLanguageServices(config: WeavieLspConfig): Promise<void> {
  // Same init-order guard as startLanguageServices (see there): our init must precede any monaco.editor touch.
  await initEditorServices();
  generation += 1;
  for (const teardown of clients.values()) {
    teardown();
  }
  clients.clear();
  started.clear();
  activeConfig = config;
  indexServers(config);
  startForOpenModels();
}

// If a server crashes (or the WS drops) while documents are open, reconnect with capped exponential backoff
// so a broken server doesn't storm; a connection that stayed up past HEALTHY_UPTIME_MS resets the backoff.
const MAX_RECONNECT_ATTEMPTS = 5;
const HEALTHY_UPTIME_MS = 10_000;

function hasOpenDocumentFor(server: WeavieLspServer): boolean {
  return monaco.editor.getModels().some((m) => server.languageIds.includes(m.getLanguageId()));
}

function describeError(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

function connect(config: WeavieLspConfig, server: WeavieLspServer, attempt = 0): void {
  const channelId = `lsp${++channelSeq}`;
  const gen = generation;
  let openedAt = 0;
  // Set on intentional teardown (session switch): the supervised reconnect stands down and a late exit is ignored.
  let torn = false;
  // Set once this attempt's outcome is decided, so a failed start and a server exit schedule at most one reconnect.
  let handled = false;
  let client: MonacoLanguageClient | undefined;
  // `channel` and `startPromise` are const, assigned further down; the teardown closures here forward-reference
  // them, which is safe because nothing calls these closures until this synchronous body has finished.

  const disposeClient = (): void => {
    const c = client;
    client = undefined;
    if (c === undefined) {
      return;
    }
    // dispose() rejects while the client is still 'starting'; wait for start to settle, then dispose, and
    // swallow either rejection — we're tearing down regardless. (This is the source of the stray
    // "Client is not running ... state is: starting" unhandled rejections.)
    void Promise.allSettled([startPromise])
      .then(() => c.dispose())
      .catch(() => {});
  };

  const superviseReconnect = (reason: string): void => {
    // Stand down if this client was torn down, or a switch happened (a newer generation owns the bridge now).
    if (torn || gen !== generation) {
      return;
    }
    if (!hasOpenDocumentFor(server)) {
      started.delete(server.id); // no document needs it — let a future open restart it
      clients.delete(server.id);
      return;
    }
    const nextAttempt = openedAt > 0 && Date.now() - openedAt > HEALTHY_UPTIME_MS ? 1 : attempt + 1;
    if (nextAttempt > MAX_RECONNECT_ATTEMPTS) {
      started.delete(server.id);
      clients.delete(server.id);
      log(
        "error",
        `lsp: ${server.id} gave up after ${MAX_RECONNECT_ATTEMPTS} reconnects (${reason})`,
      );
      notify(
        "error",
        `${server.id} language intelligence is unavailable (${reason}). Check that its language server is installed and on PATH.`,
      );
      return;
    }
    // First failure of a streak (the initial drop, or one after a healthy session): a self-dismissing warn so
    // the user sees the hiccup immediately rather than only after the whole backoff budget runs out.
    if (nextAttempt === 1) {
      notify("warn", `${server.id} language intelligence interrupted (${reason}); reconnecting…`);
    }
    const delayMs = Math.min(1000 * 2 ** (nextAttempt - 1), 15_000);
    log(
      "warn",
      `lsp: ${server.id} ${reason}; reconnecting in ${delayMs}ms (attempt ${nextAttempt})`,
    );
    setTimeout(() => {
      if (gen === generation && hasOpenDocumentFor(server)) {
        connect(config, server, nextAttempt); // stays in `started` across the retry
      } else {
        started.delete(server.id);
      }
    }, delayMs);
  };

  // One funnel for every failure path — a failed initialize or a server exit/failure-to-start — so recovery (and
  // the warn/give-up toasts) runs exactly once per attempt.
  const fail = (reason: string): void => {
    if (handled) {
      return;
    }
    handled = true;
    disposeClient();
    channel?.dispose();
    superviseReconnect(reason);
  };

  const teardown = (): void => {
    torn = true;
    disposeClient();
    channel?.dispose();
  };
  clients.set(server.id, teardown);

  // Open the bridge channel: the host spawns the server on lsp-start; its exit or failure-to-start arrives via
  // onExit (carrying the host-side reason), routed through the same supervised reconnect as a dropped link. No
  // socket handshake, so the channel is usable immediately — the client sends `initialize` once start() listens.
  const channel = openLspChannel(config.slot, server.id, channelId, (code, reason) => {
    if (torn || gen !== generation) {
      return;
    }
    fail(reason ?? `server exited (code ${code})`);
  });
  openedAt = Date.now();

  const settings = server.settings ?? {};
  client = new MonacoLanguageClient({
    name: `Weavie ${server.id} language client`,
    clientOptions: {
      documentSelector: server.languageIds,
      workspaceFolder: {
        uri: monaco.Uri.file(config.workspace),
        name: "weavie",
        index: 0,
      },
      // Feed the server its defaults both ways — initializationOptions and workspace/configuration answers
      // (some servers gate features on config, e.g. gopls semantic tokens). No VSCode config service (§18).
      initializationOptions: settings,
      middleware: {
        workspace: {
          configuration: (params) => params.items.map(() => settings),
        },
      },
      // The client itself stays passive on errors; recovery is the host-supervised reconnect above.
      errorHandler: {
        error: () => ({ action: ErrorAction.Continue }),
        closed: () => ({ action: CloseAction.DoNotRestart }),
      },
    },
    messageTransports: { reader: channel.reader, writer: channel.writer },
  });

  // start() rejects when the server faults on initialize (e.g. csharp-ls with no resolvable SDK). Route that
  // through the same reconnect/give-up path as a server exit instead of leaking an unhandled rejection.
  const startPromise = client.start();
  void startPromise.then(
    () => {
      log("info", `lsp: ${server.id} client started`);
      notifyLanguageClientStarted();
    },
    (err: unknown) => fail(`initialize failed: ${describeError(err)}`),
  );
}
