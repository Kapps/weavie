// The LSP transport that rides the Weavie bridge instead of a dedicated WebSocket. Each language client gets a
// page-minted `channel`; outbound JSON-RPC is posted as `lsp-data` (payload embedded — it's already JSON), and
// the host routes the server's stdout frames back as `lsp-data` (demuxed here by channel) and its exit as
// `lsp-exit`. Because there is no socket of its own, LSP rides the transport of the backend that OWNS the
// channel's slot (in-process, WebSocket, or TLS-proxied) — which is what lets it reach remote sessions.
// Never the page's active backend: mid-handoff, a client's frames (including its teardown's shutdown/exit and
// lsp-stop) would land on the incoming backend, which doesn't know the slot — misrouted requests and a leaked
// server on the owner.

import {
  AbstractMessageReader,
  AbstractMessageWriter,
  type DataCallback,
  type Disposable,
  type Message,
  type MessageReader,
  type MessageWriter,
} from "vscode-jsonrpc";
import { backendPhase, onHostMessage, postToBackend } from "../bridge";

// A MessageReader fed by the demux: the language client calls listen() with its callback; the demux calls
// deliver()/close() as `lsp-data`/`lsp-exit` arrive for this channel.
class BridgeMessageReader extends AbstractMessageReader implements MessageReader {
  private callback: DataCallback | undefined;

  listen(callback: DataCallback): Disposable {
    this.callback = callback;
    return {
      dispose: () => {
        this.callback = undefined;
      },
    };
  }

  deliver(message: Message): void {
    this.callback?.(message);
  }

  close(): void {
    this.fireClose();
  }
}

// A MessageWriter that posts each JSON-RPC message as an `lsp-data` to the channel's owning backend. While
// that backend's link is down it rejects rather than buffering into the bridge's unbounded outbox — a loud
// failure the client's supervised reconnect handles, with the reconnecting banner already on screen (no
// silent fallback, no unbounded growth).
class BridgeMessageWriter extends AbstractMessageWriter implements MessageWriter {
  private errorCount = 0;

  constructor(
    private readonly backendId: string,
    private readonly slot: string,
    private readonly channel: string,
  ) {
    super();
  }

  async write(message: Message): Promise<void> {
    if (backendPhase(this.backendId) !== "online") {
      const error = new Error("the backend is offline; LSP message not sent");
      this.fireError([error, message, ++this.errorCount]);
      throw error;
    }

    postToBackend(this.backendId, {
      type: "lsp-data",
      slot: this.slot,
      channel: this.channel,
      payload: message,
    });
  }

  end(): void {}
}

// Live channels by id, so an inbound lsp-data/lsp-exit finds its reader. The single bridge subscription is wired
// lazily on the first channel.
const readers = new Map<string, BridgeMessageReader>();
const exitHandlers = new Map<string, (code: number, reason: string | undefined) => void>();
let subscribed = false;

function ensureSubscribed(): void {
  if (subscribed) {
    return;
  }
  subscribed = true;
  onHostMessage((message) => {
    if (message.type === "lsp-data") {
      readers.get(message.channel)?.deliver(message.payload as Message);
    } else if (message.type === "lsp-exit") {
      exitHandlers.get(message.channel)?.(message.code, message.reason);
      readers.get(message.channel)?.close();
    }
  });
}

/** A bridge-backed transport pair for one language client, plus a teardown that stops its server. */
export interface LspBridgeChannel {
  reader: MessageReader;
  writer: MessageWriter;
  dispose: () => void;
}

/**
 * Opens an LSP channel on `backendId` — the backend that owns `slot`: registers the demux, asks that host to
 * spawn `server` on `slot`, and returns the reader/writer to hand `monaco-languageclient`. `onExit` fires when
 * the host reports the server ended or never started (carrying its reason, e.g. "no server on PATH").
 * `dispose` stops the server on that same backend and unregisters.
 */
export function openLspChannel(
  backendId: string,
  slot: string,
  server: string,
  channel: string,
  onExit: (code: number, reason: string | undefined) => void,
): LspBridgeChannel {
  ensureSubscribed();
  const reader = new BridgeMessageReader();
  const writer = new BridgeMessageWriter(backendId, slot, channel);
  readers.set(channel, reader);
  exitHandlers.set(channel, onExit);
  postToBackend(backendId, { type: "lsp-start", slot, server, channel });
  return {
    reader,
    writer,
    dispose: () => {
      readers.delete(channel);
      exitHandlers.delete(channel);
      postToBackend(backendId, { type: "lsp-stop", slot, channel });
    },
  };
}
