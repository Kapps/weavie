// The WebView half of the language-server bridge (spec §10): monaco-languageclient connected to the host's
// loopback WS↔stdio proxy via injected `window.__WEAVIE_LSP__`. A client starts lazily — only when a
// document of its language first opens — so csharp-ls/gopls aren't spawned until a .cs/.go file appears.
//
// Each session has its OWN bridge (port, token, worktree root). A session switch pushes the incoming config
// and `rebindLanguageServices` tears every client down and reconnects, so intelligence follows the worktree.

import * as monaco from "monaco-editor";
import { MonacoLanguageClient } from "monaco-languageclient";
import { CloseAction, ErrorAction } from "vscode-languageclient";
import { WebSocketMessageReader, WebSocketMessageWriter, toSocket } from "vscode-ws-jsonrpc";
import { log } from "../bridge";
import { notify } from "../notify/notify";

/** One language server the host offers: bridge selector id, the languages it serves, and its defaults. */
export interface WeavieLspServer {
  id: string;
  languageIds: string[];
  settings: Record<string, unknown> | null;
}

/** LSP bridge discovery the C# host injects (loopback WS endpoint + per-session token + workspace). */
export interface WeavieLspConfig {
  url: string;
  token: string;
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
// Server id → teardown (dispose the client + close its socket). A session switch tears every client down.
const clients = new Map<string, () => void>();
// The active session's bridge config, read live (not captured) so lazy starts after a switch use the new
// bridge. Undefined until the host injects/pushes one.
let activeConfig: WeavieLspConfig | undefined;
let serverByLanguage = new Map<string, WeavieLspServer>();
let modelHooksInstalled = false;
// Bumped on every rebind; a connect() captures its generation and stands down if a switch superseded it.
let generation = 0;

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
export function startLanguageServices(): void {
  const config = window.__WEAVIE_LSP__;
  if (config === undefined) {
    return;
  }
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
export function rebindLanguageServices(config: WeavieLspConfig): void {
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
  const url = `${config.url}/${server.id}?token=${encodeURIComponent(config.token)}`;
  const webSocket = new WebSocket(url);
  const gen = generation;
  let openedAt = 0;
  // Set on intentional teardown (session switch): the supervised reconnect stands down and a late onopen closes.
  let torn = false;
  // Set once this attempt's outcome is decided, so a failed start, a closed reader, and a ws error together
  // schedule at most one reconnect.
  let handled = false;
  let client: MonacoLanguageClient | undefined;
  let startPromise: Promise<void> | undefined;

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

  // One funnel for every failure path — a failed initialize, a dropped connection, a pre-open ws error — so
  // recovery (and the eventual give-up toast) runs exactly once per attempt.
  const fail = (reason: string): void => {
    if (handled) {
      return;
    }
    handled = true;
    disposeClient();
    try {
      webSocket.close();
    } catch {
      // already closing/closed
    }
    superviseReconnect(reason);
  };

  const teardown = (): void => {
    torn = true;
    disposeClient();
    try {
      webSocket.close();
    } catch {
      // already closing/closed — nothing to do
    }
  };
  clients.set(server.id, teardown);

  webSocket.onopen = (): void => {
    if (torn || gen !== generation) {
      // A switch landed between opening the socket and it connecting — drop this stale connection.
      try {
        webSocket.close();
      } catch {
        // already closing
      }
      return;
    }
    openedAt = Date.now();
    const socket = toSocket(webSocket);
    const reader = new WebSocketMessageReader(socket);
    const writer = new WebSocketMessageWriter(socket);
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
        // The client itself stays passive on errors; recovery is the host-supervised reconnect below.
        errorHandler: {
          error: () => ({ action: ErrorAction.Continue }),
          closed: () => ({ action: CloseAction.DoNotRestart }),
        },
      },
      messageTransports: { reader, writer },
    });

    // start() rejects when the server faults on initialize (e.g. csharp-ls with no resolvable SDK). Route that
    // through the same reconnect/give-up path as a dropped connection instead of leaking an unhandled rejection.
    startPromise = client.start();
    startPromise.then(
      () => log("info", `lsp: ${server.id} client started`),
      (err: unknown) => fail(`initialize failed: ${describeError(err)}`),
    );
    reader.onClose(() => fail("connection closed"));
  };

  webSocket.onerror = (): void => {
    if (openedAt === 0) {
      fail("websocket error (is the server installed on PATH?)");
    }
  };
}
