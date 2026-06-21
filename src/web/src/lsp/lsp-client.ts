// The WebView half of the language-server bridge (spec §10): monaco-languageclient connected to the
// host's loopback WS↔stdio proxy. The host injects `window.__WEAVIE_LSP__` (url, token, workspace,
// and the server catalog) before navigation; we open `ws://127.0.0.1:PORT/<serverId>?token=…` and the
// host spawns + pipes the matching language server. A client is started lazily — only when a document
// of its language is first opened — so we never spawn csharp-ls/gopls until a .cs/.go file appears.
// LSP I/O is fully async; it never touches the keystroke hot path (§15).
//
// Each session has its OWN bridge (its own port, token, and workspace root, rooted at its worktree). On a
// session switch the host pushes the incoming session's config and we `rebindLanguageServices` — tearing
// every client down and reconnecting against the new bridge — so language intelligence follows the focused
// session's worktree instead of staying pinned to the one we loaded with.

import * as monaco from "monaco-editor";
import { MonacoLanguageClient } from "monaco-languageclient";
import { CloseAction, ErrorAction } from "vscode-languageclient";
import { WebSocketMessageReader, WebSocketMessageWriter, toSocket } from "vscode-ws-jsonrpc";
import { log } from "../bridge";

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
// The active session's bridge config. maybeStart + the onDidCreateModel hook read this (not a captured value),
// so lazy starts after a switch use the new bridge. Undefined until the host injects/pushes one.
let activeConfig: WeavieLspConfig | undefined;
let serverByLanguage = new Map<string, WeavieLspServer>();
let modelHooksInstalled = false;
// Bumped on every rebind. A connect() captures the generation it began in; a supervised reconnect scheduled
// before a switch checks it and stands down rather than reconnecting to the now-previous session's bridge.
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
 * Wires lazy, per-language LSP for the session loaded at launch. For every server the host advertises, a
 * client connects the first time a matching document is open. No-op when the host hasn't injected bridge
 * config (plain-browser dev) — the editor still works, just without language intelligence.
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
 * Rebind language services to a different session's bridge on a session switch. Every existing client is
 * pinned to the previous session's url/token and workspace root, so tear them all down and reconnect against
 * the incoming session's bridge — spawning ITS language servers rooted at ITS worktree, lazily for whatever
 * documents are currently open. A no-op rebind (same config) is still a clean reconnect; switches are rare.
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

// Supervision: if a server crashes (or the WS drops) while documents are open, reconnect — each
// reconnect spawns a fresh subprocess on the host and re-initializes — with exponential backoff,
// capped, so a fundamentally broken server doesn't storm. A connection that stayed up a while is
// considered healthy and resets the backoff.
const MAX_RECONNECT_ATTEMPTS = 5;
const HEALTHY_UPTIME_MS = 10_000;

function hasOpenDocumentFor(server: WeavieLspServer): boolean {
  return monaco.editor.getModels().some((m) => server.languageIds.includes(m.getLanguageId()));
}

function connect(config: WeavieLspConfig, server: WeavieLspServer, attempt = 0): void {
  const url = `${config.url}/${server.id}?token=${encodeURIComponent(config.token)}`;
  const webSocket = new WebSocket(url);
  const gen = generation;
  let openedAt = 0;
  // Set when this client is intentionally torn down (session switch): the supervised reconnect stands down so
  // it can't reconnect to the now-previous session's bridge, and a late onopen closes instead of starting.
  let torn = false;
  let client: MonacoLanguageClient | undefined;

  const teardown = (): void => {
    torn = true;
    client?.dispose();
    try {
      webSocket.close();
    } catch {
      // already closing/closed — nothing to do
    }
  };
  clients.set(server.id, teardown);

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
        // Feed the server its defaults both ways: as initializationOptions and as the answer to its
        // workspace/configuration requests (some servers gate features on config — gopls semantic
        // tokens). We don't run the VSCode configuration service (guardrail §18), so we answer directly.
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

    client.start();
    log("info", `lsp: ${server.id} client started`);
    reader.onClose(() => {
      client?.dispose();
      superviseReconnect("connection closed");
    });
  };

  webSocket.onerror = (): void => {
    if (openedAt === 0) {
      superviseReconnect("websocket error (is the server installed on PATH?)");
    }
  };
}
