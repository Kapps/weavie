import { describe, expect, it, vi } from "vitest";
import {
  type ClientSession,
  HostConnection,
  type HostHello,
  type SessionCatalogEntry,
} from "./host-connection";
import { type MessageEnvelope, parseEnvelope, type SessionAddress } from "./message-envelope";

const address = (slot: string, incarnation: string): SessionAddress => ({ slot, incarnation });

const entry = (id: string, session: SessionAddress): SessionCatalogEntry => ({
  id,
  label: id,
  address: session,
  loaded: true,
  providerId: "codex",
  agentSurface: "structured",
  agentInputProtocol: 1,
  status: "idle",
  hue: 0,
  monogram: id.slice(0, 1),
});

const hello = (hostIncarnation: string, sessions: SessionCatalogEntry[]): HostHello => ({
  hostIncarnation,
  buildNumber: "test",
  sessions,
  layout: {},
  remoteAgents: [],
  rail: { lastLocation: "", promoted: [], selected: null },
  search: {
    options: {
      caseSensitive: false,
      wholeWord: false,
      regex: false,
      excludeGitignored: true,
      include: "",
      exclude: "",
    },
    recentTerms: [],
  },
  testProfile: "",
  commandCatalog: { commands: [], keybindings: [] },
});

const response = (
  request: MessageEnvelope,
  payload: unknown,
  error: string | null = null,
): MessageEnvelope => ({
  scope: request.scope,
  session: request.session,
  kind: "response",
  requestId: request.requestId,
  feature: request.feature,
  name: request.name,
  payload,
  error,
});

const event = (
  session: SessionAddress,
  feature: string,
  name: string,
  payload: unknown,
): MessageEnvelope => ({
  scope: "session",
  session,
  kind: "event",
  requestId: null,
  feature,
  name,
  payload,
  error: null,
});

function harness(initialHello: HostHello) {
  const sent: MessageEnvelope[] = [];
  const errors: unknown[] = [];
  let nextHello = initialHello;
  let connection: HostConnection;
  connection = new HostConnection(
    "local",
    "Local",
    true,
    (json) => {
      const envelope = parseEnvelope(json);
      if (envelope === null) {
        throw new Error("Client emitted an invalid envelope.");
      }
      sent.push(envelope);
      if (envelope.feature === "connection" && envelope.name === "hello") {
        queueMicrotask(() => connection.receive(response(envelope, nextHello)));
      } else if (envelope.feature === "lifecycle" && envelope.name === "sync") {
        queueMicrotask(() => connection.receive(response(envelope, { ok: true })));
      }
    },
    (error) => errors.push(error),
  );
  return {
    connection,
    sent,
    errors,
    setHello(value: HostHello) {
      nextHello = value;
    },
  };
}

describe("HostConnection session ownership", () => {
  it("buffers exact-session events until the hello catalog creates their owner", async () => {
    const primary = address("primary", "one");
    const host = harness(hello("host-one", [entry("primary", primary)]));
    const connecting = host.connection.connect();
    host.connection.receive(
      event(primary, "lsp", "config", {
        workspace: "/one",
        servers: [],
      }),
    );

    await connecting;
    await vi.waitFor(() =>
      expect(host.connection.session(primary)?.state.lsp.current?.workspace).toBe("/one"),
    );

    expect(host.errors).toEqual([]);
  });

  it("buffers a new session's first event until its catalog handler creates the owner", async () => {
    const primary = address("primary", "one");
    const background = address("background", "two");
    const host = harness(hello("host-one", [entry("primary", primary)]));
    await host.connection.connect();
    let calls = 0;
    const installed = new WeakSet<object>();
    host.connection.onCatalog((_catalog, sessions) => {
      const session = sessions.find(
        (candidate) =>
          candidate.address.slot === background.slot &&
          candidate.address.incarnation === background.incarnation,
      );
      if (session !== undefined && !installed.has(session)) {
        installed.add(session);
        session.feature("dummy").on("changed", () => {
          calls += 1;
        });
      }
    });

    host.connection.receive({
      scope: "host",
      session: null,
      kind: "event",
      requestId: null,
      feature: "sessions",
      name: "catalog",
      payload: [entry("primary", primary), entry("background", background)],
      error: null,
    });
    host.connection.receive(event(background, "dummy", "changed", {}));

    await vi.waitFor(() => expect(calls).toBe(1));
    expect(host.errors).toEqual([]);
  });

  it("discards a connecting session event absent from the authoritative hello catalog", async () => {
    const primary = address("primary", "one");
    const background = address("background", "two");
    const host = harness(hello("host-one", [entry("primary", primary)]));
    const connecting = host.connection.connect();
    host.connection.receive(event(background, "dummy", "changed", {}));
    await connecting;
    let calls = 0;
    host.connection.onCatalog((_catalog, sessions) => {
      const session = sessions.find(
        (candidate) =>
          candidate.address.slot === background.slot &&
          candidate.address.incarnation === background.incarnation,
      );
      session?.feature("dummy").on("changed", () => {
        calls += 1;
      });
    });

    host.connection.receive({
      scope: "host",
      session: null,
      kind: "event",
      requestId: null,
      feature: "sessions",
      name: "catalog",
      payload: [entry("primary", primary), entry("background", background)],
      error: null,
    });

    await Promise.resolve();
    expect(calls).toBe(0);
    expect(host.errors).toEqual([]);
  });

  it("discards an unknown event once all prior catalog work has settled", async () => {
    const primary = address("primary", "one");
    const unknown = address("background", "two");
    const host = harness(hello("host-one", [entry("primary", primary)]));
    await host.connection.connect();

    host.connection.receive(event(unknown, "dummy", "changed", {}));
    await Promise.resolve();
    let calls = 0;
    host.connection.onCatalog((_catalog, sessions) => {
      const session = sessions.find(
        (candidate) =>
          candidate.address.slot === unknown.slot &&
          candidate.address.incarnation === unknown.incarnation,
      );
      session?.feature("dummy").on("changed", () => {
        calls += 1;
      });
    });
    host.connection.receive({
      scope: "host",
      session: null,
      kind: "event",
      requestId: null,
      feature: "sessions",
      name: "catalog",
      payload: [entry("primary", primary), entry("background", unknown)],
      error: null,
    });
    await Promise.resolve();

    expect(calls).toBe(0);
    expect(host.errors).toEqual([]);
  });

  it("does not let an earlier unknown frame discard a later frame behind its own catalog", async () => {
    const primary = address("primary", "one");
    const unknown = address("unknown", "stale");
    const background = address("background", "two");
    const host = harness(hello("host-one", [entry("primary", primary)]));
    await host.connection.connect();
    let calls = 0;
    host.connection.onCatalog((_catalog, sessions) => {
      const session = sessions.find(
        (candidate) =>
          candidate.address.slot === background.slot &&
          candidate.address.incarnation === background.incarnation,
      );
      session?.feature("dummy").on("changed", () => {
        calls += 1;
      });
    });

    host.connection.receive(event(unknown, "dummy", "changed", {}));
    host.connection.receive({
      scope: "host",
      session: null,
      kind: "event",
      requestId: null,
      feature: "sessions",
      name: "catalog",
      payload: [entry("primary", primary), entry("background", background)],
      error: null,
    });
    host.connection.receive(event(background, "dummy", "changed", {}));

    await vi.waitFor(() => expect(calls).toBe(1));
    expect(host.errors).toEqual([]);
  });

  it("does not let one catalog subscriber block the remaining session features", async () => {
    const primary = address("primary", "one");
    const background = address("background", "two");
    const host = harness(hello("host-one", [entry("primary", primary)]));
    await host.connection.connect();
    let fail = false;
    host.connection.onCatalog(() => {
      if (fail) {
        throw new Error("broken catalog subscriber");
      }
    });
    let observed: readonly ClientSession[] = [];
    host.connection.onCatalog((_catalog, sessions) => {
      observed = sessions;
    });

    fail = true;
    host.connection.receive({
      scope: "host",
      session: null,
      kind: "event",
      requestId: null,
      feature: "sessions",
      name: "catalog",
      payload: [entry("primary", primary), entry("background", background)],
      error: null,
    });
    await vi.waitFor(() => expect(observed).toContain(host.connection.session(background)));
    expect(host.errors).toEqual([
      expect.objectContaining({ message: "broken catalog subscriber" }),
    ]);
  });

  it("isolates subscribers that fail during their immediate catalog and hello snapshots", async () => {
    const primary = address("primary", "one");
    const host = harness(hello("host-one", [entry("primary", primary)]));
    await host.connection.connect();
    let catalogObserved = false;
    let helloObserved = false;

    expect(() =>
      host.connection.onCatalog(() => {
        throw new Error("broken initial catalog subscriber");
      }),
    ).not.toThrow();
    host.connection.onCatalog(() => {
      catalogObserved = true;
    });
    expect(() =>
      host.connection.onHello(() => {
        throw new Error("broken initial hello subscriber");
      }),
    ).not.toThrow();
    host.connection.onHello(() => {
      helloObserved = true;
    });

    expect(catalogObserved).toBe(true);
    expect(helloObserved).toBe(true);
    expect(host.errors).toEqual([
      expect.objectContaining({ message: "broken initial catalog subscriber" }),
      expect.objectContaining({ message: "broken initial hello subscriber" }),
    ]);
  });

  it("delivers an initial hello once to a listener installed from that hello's catalog", async () => {
    const primary = address("primary", "one");
    const host = harness(hello("host-one", [entry("primary", primary)]));
    let installed = false;
    let calls = 0;
    host.connection.onCatalog((_catalog, sessions) => {
      if (!installed && sessions.length > 0) {
        installed = true;
        host.connection.onHello(() => {
          calls += 1;
        });
      }
    });

    await host.connection.connect();

    expect(calls).toBe(1);
    expect(host.errors).toEqual([]);
  });

  it("closes an old incarnation and never routes its later messages to a reused slot", async () => {
    const oldAddress = address("primary", "old");
    const newAddress = address("primary", "new");
    const host = harness(hello("host-one", [entry("primary", oldAddress)]));
    await host.connection.connect();
    const oldSession = host.connection.session(oldAddress);
    expect(oldSession).toBeDefined();
    if (oldSession === undefined) {
      throw new Error("The old session was not created.");
    }
    let staleCalls = 0;
    oldSession.feature("dummy").on("changed", () => {
      staleCalls += 1;
    });
    const pending = oldSession.feature("dummy").request("wait", {});

    host.connection.receive({
      scope: "host",
      session: null,
      kind: "event",
      requestId: null,
      feature: "sessions",
      name: "catalog",
      payload: [entry("primary", newAddress)],
      error: null,
    });
    await expect(pending).rejects.toThrow("no longer live");
    host.connection.receive(event(oldAddress, "dummy", "changed", {}));
    await Promise.resolve();

    expect(staleCalls).toBe(0);
    expect(host.connection.session(oldAddress)).toBeUndefined();
    expect(host.connection.session(newAddress)).toBeDefined();
    expect(host.connection.session(newAddress)).not.toBe(oldSession);
  });

  it("holds reconnect traffic until the replacement catalog validates its address", async () => {
    const primary = address("primary", "one");
    const host = harness(hello("host-one", [entry("primary", primary)]));
    await host.connection.connect();
    const session = host.connection.session(primary);
    expect(session).toBeDefined();
    let calls = 0;
    session?.feature("dummy").on("changed", () => {
      calls += 1;
    });

    host.connection.transportDropped();
    host.connection.receive(event(primary, "dummy", "changed", {}));
    expect(calls).toBe(0);
    host.setHello(hello("host-two", [entry("primary", primary)]));
    await host.connection.connect();
    await Promise.resolve();

    expect(host.connection.session(primary)).toBe(session);
    expect(calls).toBe(1);
    expect(host.errors).toEqual([]);
  });

  it("does not deliver a reconnect hello to a listener removed by its replacement catalog", async () => {
    const primary = address("primary", "one");
    const host = harness(hello("host-one", [entry("primary", primary)]));
    await host.connection.connect();
    let calls = 0;
    const offHello = host.connection.onHello(() => {
      calls += 1;
    });
    host.connection.onCatalog((_catalog, sessions) => {
      if (!sessions.some((session) => session.address.slot === primary.slot)) {
        offHello();
      }
    });

    host.connection.transportDropped();
    host.setHello(hello("host-two", []));
    await host.connection.connect();

    expect(calls).toBe(1);
    expect(host.errors).toEqual([]);
  });
});
