import {
  AbstractMessageReader,
  AbstractMessageWriter,
  type DataCallback,
  type Disposable,
  type Message,
  type MessageReader,
  type MessageWriter,
} from "vscode-jsonrpc";
import type { ClientSession } from "../bridge";

class BusMessageReader extends AbstractMessageReader implements MessageReader {
  private callback: DataCallback | undefined;
  private closed = false;

  listen(callback: DataCallback): Disposable {
    this.callback = callback;
    return { dispose: () => (this.callback = undefined) };
  }

  deliver(message: Message): void {
    this.callback?.(message);
  }

  close(): void {
    if (this.closed) {
      return;
    }
    this.closed = true;
    this.fireClose();
  }
}

class BusMessageWriter extends AbstractMessageWriter implements MessageWriter {
  private errorCount = 0;

  constructor(
    private readonly session: ClientSession,
    private readonly channel: string,
  ) {
    super();
  }

  async write(message: Message): Promise<void> {
    try {
      this.session.feature("lsp").publish("data", { channel: this.channel, payload: message });
    } catch (error) {
      const failure = error instanceof Error ? error : new Error(String(error));
      this.fireError([failure, message, ++this.errorCount]);
      throw failure;
    }
  }

  end(): void {}
}

export interface LspBridgeChannel {
  reader: MessageReader;
  writer: MessageWriter;
  ready: Promise<void>;
  dispose: () => void;
}

export class LspStartError extends Error {}

export function openLspChannel(
  session: ClientSession,
  server: string,
  channel: string,
  onExit: (code: number, reason: string | undefined) => void,
): LspBridgeChannel {
  const feature = session.feature("lsp");
  const reader = new BusMessageReader();
  const offData = feature.on<{ channel: string; payload: Message }>("data", (message) => {
    if (message.channel === channel) {
      reader.deliver(message.payload);
    }
  });
  const offExit = feature.on<{ channel: string; code: number; reason?: string }>(
    "exit",
    (message) => {
      if (message.channel === channel) {
        onExit(message.code, message.reason);
        reader.close();
      }
    },
  );
  const ready = feature
    .request<{ ok: boolean; error?: string }, { server: string; channel: string }>("start", {
      server,
      channel,
    })
    .then((result) => {
      if (!result.ok) {
        throw new LspStartError(result.error ?? `${server} failed to start.`);
      }
    });
  let disposed = false;
  return {
    reader,
    writer: new BusMessageWriter(session, channel),
    ready,
    dispose: () => {
      if (disposed) {
        return;
      }
      disposed = true;
      offData();
      offExit();
      try {
        feature.publish("stop", { channel });
      } catch {
        // The owning session already closed, which also stopped its language servers.
      }
      reader.close();
    },
  };
}
