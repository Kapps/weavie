// The WebView half of the language-server bridge (spec §10): a monaco-languageclient connected to the
// host's loopback WS↔stdio proxy. The host injects `window.__WEAVIE_LSP__ = { url, token, workspace }`
// before navigation; we open `ws://127.0.0.1:PORT/<serverId>?token=…`, and the host spawns + pipes the
// matching language server. LSP I/O is fully async — it never touches the keystroke hot path (§15).

import * as monaco from "monaco-editor";
import { MonacoLanguageClient } from "monaco-languageclient";
import { CloseAction, ErrorAction } from "vscode-languageclient";
import { WebSocketMessageReader, WebSocketMessageWriter, toSocket } from "vscode-ws-jsonrpc";
import { log } from "../bridge";

interface LspBridgeConfig {
  /** Base loopback URL of the host bridge, e.g. "ws://127.0.0.1:54123". */
  url: string;
  /** Per-session token required as the `token` query parameter on the upgrade. */
  token: string;
  /** Absolute workspace root the servers are rooted at (used for the client's workspaceFolder). */
  workspace: string;
}

function bridgeConfig(): LspBridgeConfig | undefined {
  return window.__WEAVIE_LSP__;
}

/**
 * Starts a language client for `serverId` (the bridge URL selector / recipe id), attaching to open
 * documents matching `documentSelector`. No-op when the host hasn't injected bridge config (e.g. a
 * plain-browser dev run) — the editor still works, just without language intelligence.
 */
export function startLanguageClient(serverId: string, documentSelector: string[]): void {
  const config = bridgeConfig();
  if (config === undefined) {
    return;
  }

  const url = `${config.url}/${serverId}?token=${encodeURIComponent(config.token)}`;
  const webSocket = new WebSocket(url);

  webSocket.onopen = (): void => {
    const socket = toSocket(webSocket);
    const reader = new WebSocketMessageReader(socket);
    const writer = new WebSocketMessageWriter(socket);
    const client = new MonacoLanguageClient({
      name: `Weavie ${serverId} language client`,
      clientOptions: {
        documentSelector,
        workspaceFolder: {
          uri: monaco.Uri.file(config.workspace),
          name: "weavie",
          index: 0,
        },
        // Keep the client resilient: surface, don't crash, and don't auto-restart on close (the host
        // owns server lifecycle/supervision — that's M4).
        errorHandler: {
          error: () => ({ action: ErrorAction.Continue }),
          closed: () => ({ action: CloseAction.DoNotRestart }),
        },
      },
      messageTransports: { reader, writer },
    });

    client.start();
    log("info", `lsp: ${serverId} client started`);
    reader.onClose(() => client.dispose());
  };

  webSocket.onerror = (): void => {
    log("warn", `lsp: ${serverId} websocket error (is the server installed on PATH?)`);
  };
}
