import { beforeEach, describe, expect, it } from "vitest";
import type { Message } from "vscode-jsonrpc";
import type { ClientSession } from "../bridge";
import { MessageBus } from "../messaging/message-bus";
import { parseEnvelope, type SessionAddress } from "../messaging/message-envelope";
import { openLspChannel } from "./lsp-bridge-transport";

interface BusPair {
  session: ClientSession;
  client: MessageBus;
  host: MessageBus;
}

function pair(address: SessionAddress): BusPair {
  let client: MessageBus;
  let host: MessageBus;
  client = new MessageBus("session", address, (json) => {
    const envelope = parseEnvelope(json);
    if (envelope !== null) {
      host.receive(envelope);
    }
  });
  host = new MessageBus("session", address, (json) => {
    const envelope = parseEnvelope(json);
    if (envelope !== null) {
      client.receive(envelope);
    }
  });
  return {
    client,
    host,
    session: {
      feature: (name: string) => client.feature(name),
    } as ClientSession,
  };
}

async function settle(): Promise<void> {
  for (let index = 0; index < 10; index += 1) {
    await Promise.resolve();
  }
}

let owner: BusPair;

beforeEach(() => {
  owner = pair({ slot: "a", incarnation: "a1" });
});

describe("session-owned LSP channel", () => {
  it("starts, writes, and stops on its owner's bus", async () => {
    const events: Array<{ name: string; payload: unknown }> = [];
    const feature = owner.host.feature("lsp");
    feature.handle("start", (payload) => {
      events.push({ name: "start", payload });
      return Promise.resolve({ ok: true });
    });
    feature.on("data", (payload) => {
      events.push({ name: "data", payload });
    });
    feature.on("stop", (payload) => {
      events.push({ name: "stop", payload });
    });

    const channel = openLspChannel(owner.session, "typescript", "channel-1", () => {});
    await channel.ready;
    const message = {
      jsonrpc: "2.0",
      id: 1,
      method: "textDocument/completion",
    } as unknown as Message;
    await channel.writer.write(message);
    channel.dispose();
    await settle();

    expect(events).toEqual([
      {
        name: "start",
        payload: { server: "typescript", channel: "channel-1" },
      },
      {
        name: "data",
        payload: { channel: "channel-1", payload: message },
      },
      { name: "stop", payload: { channel: "channel-1" } },
    ]);
  });

  it("cannot cross into another session even when channel ids collide", async () => {
    const other = pair({ slot: "b", incarnation: "b1" });
    owner.host.feature("lsp").handle("start", () => Promise.resolve({ ok: true }));
    other.host.feature("lsp").handle("start", () => Promise.resolve({ ok: true }));
    const first = openLspChannel(owner.session, "csharp", "same", () => {});
    const second = openLspChannel(other.session, "csharp", "same", () => {});
    await Promise.all([first.ready, second.ready]);
    const firstReceived: Message[] = [];
    const secondReceived: Message[] = [];
    first.reader.listen((message) => firstReceived.push(message));
    second.reader.listen((message) => secondReceived.push(message));

    const response = { jsonrpc: "2.0", id: 7, result: "first" } as unknown as Message;
    owner.host.feature("lsp").publish("data", { channel: "same", payload: response });
    await settle();

    expect(firstReceived).toEqual([response]);
    expect(secondReceived).toEqual([]);
  });

  it("reports exit only to the matching channel", async () => {
    const exits: Array<{ code: number; reason: string | undefined }> = [];
    owner.host.feature("lsp").handle("start", () => Promise.resolve({ ok: true }));
    const channel = openLspChannel(owner.session, "gopls", "wanted", (code, reason) => {
      exits.push({ code, reason });
    });
    await channel.ready;
    let closed = false;
    channel.reader.onClose(() => {
      closed = true;
    });

    owner.host.feature("lsp").publish("exit", { channel: "other", code: 2, reason: "other" });
    owner.host
      .feature("lsp")
      .publish("exit", { channel: "wanted", code: 1, reason: "not on PATH" });
    await settle();

    expect(exits).toEqual([{ code: 1, reason: "not on PATH" }]);
    expect(closed).toBe(true);
  });

  it("fails writes after its session closes", async () => {
    owner.host.feature("lsp").handle("start", () => Promise.resolve({ ok: true }));
    const channel = openLspChannel(owner.session, "typescript", "closed", () => {});
    await channel.ready;
    owner.client.close("closed");

    await expect(channel.writer.write({ jsonrpc: "2.0" } as unknown as Message)).rejects.toThrow(
      "closed",
    );
  });

  it("stops receiving after disposal", async () => {
    owner.host.feature("lsp").handle("start", () => Promise.resolve({ ok: true }));
    const channel = openLspChannel(owner.session, "typescript", "disposed", () => {});
    await channel.ready;
    const received: Message[] = [];
    let closed = false;
    channel.reader.listen((message) => received.push(message));
    channel.reader.onClose(() => {
      closed = true;
    });
    channel.dispose();

    owner.host.feature("lsp").publish("data", { channel: "disposed", payload: { jsonrpc: "2.0" } });
    await settle();

    expect(received).toEqual([]);
    expect(closed).toBe(true);
  });

  it("reports a correlated permanent start failure without creating an exit event", async () => {
    owner.host
      .feature("lsp")
      .handle("start", () => Promise.resolve({ ok: false, error: "not on PATH" }));

    const channel = openLspChannel(owner.session, "typescript", "missing", () => {});

    await expect(channel.ready).rejects.toThrow("not on PATH");
  });
});
