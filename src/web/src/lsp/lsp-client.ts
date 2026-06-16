// The WebView half of the language-server bridge (spec §10): monaco-languageclient connected to the
// host's loopback WS↔stdio proxy. The host injects `window.__WEAVIE_LSP__` (url, token, workspace,
// and the server catalog) before navigation; we open `ws://127.0.0.1:PORT/<serverId>?token=…` and the
// host spawns + pipes the matching language server. A client is started lazily — only when a document
// of its language is first opened — so we never spawn csharp-ls/gopls until a .cs/.go file appears.
// LSP I/O is fully async; it never touches the keystroke hot path (§15).

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

const started = new Set<string>();

/**
 * Wires lazy, per-language LSP. For every server the host advertises, a client connects the first
 * time a matching document is open. No-op when the host hasn't injected bridge config (plain-browser
 * dev) — the editor still works, just without language intelligence.
 */
export function startLanguageServices(): void {
  const config = window.__WEAVIE_LSP__;
  if (config === undefined) {
    return;
  }

  const serverByLanguage = new Map<string, WeavieLspServer>();
  for (const server of config.servers) {
    for (const languageId of server.languageIds) {
      serverByLanguage.set(languageId, server);
    }
  }

  const maybeStart = (languageId: string): void => {
    const server = serverByLanguage.get(languageId);
    if (server !== undefined && !started.has(server.id)) {
      started.add(server.id);
      connect(config, server);
    }
  };

  for (const model of monaco.editor.getModels()) {
    maybeStart(model.getLanguageId());
  }
  monaco.editor.onDidCreateModel((model) => {
    maybeStart(model.getLanguageId());
    model.onDidChangeLanguage((e) => maybeStart(e.newLanguage));
  });
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
  let openedAt = 0;

  const superviseReconnect = (reason: string): void => {
    if (!hasOpenDocumentFor(server)) {
      started.delete(server.id); // no document needs it — let a future open restart it
      return;
    }
    const nextAttempt = openedAt > 0 && Date.now() - openedAt > HEALTHY_UPTIME_MS ? 1 : attempt + 1;
    if (nextAttempt > MAX_RECONNECT_ATTEMPTS) {
      started.delete(server.id);
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
      if (hasOpenDocumentFor(server)) {
        connect(config, server, nextAttempt); // stays in `started` across the retry
      } else {
        started.delete(server.id);
      }
    }, delayMs);
  };

  webSocket.onopen = (): void => {
    openedAt = Date.now();
    const socket = toSocket(webSocket);
    const reader = new WebSocketMessageReader(socket);
    const writer = new WebSocketMessageWriter(socket);
    const settings = server.settings ?? {};
    const client = new MonacoLanguageClient({
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
      client.dispose();
      superviseReconnect("connection closed");
    });
  };

  webSocket.onerror = (): void => {
    if (openedAt === 0) {
      superviseReconnect("websocket error (is the server installed on PATH?)");
    }
  };
}
