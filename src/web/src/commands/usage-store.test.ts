import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession, HostConnection } from "../bridge";

const bridge = vi.hoisted(() => ({
  install: null as null | ((connection: HostConnection) => () => void),
}));
vi.mock("../bridge", () => ({
  registerHostFeature: (install: typeof bridge.install) => {
    bridge.install = install;
  },
  hostConnection: vi.fn(),
}));
vi.mock("../notify/notify", () => ({ notify: vi.fn() }));
const { commandUsage, slashUsageId } = await import("./usage-store");

const week = 7 * 24 * 60 * 60 * 1000;
const snapshot = {
  enabled: true,
  halfLifeMilliseconds: week,
  entries: [{ id: "review", query: "re", weight: 2, lastUsed: 0 }],
};
let now = 0;
beforeEach(() => {
  now = 0;
  vi.spyOn(performance, "now").mockImplementation(() => now);
});

function host() {
  let changed!: (snapshot: unknown) => void;
  let hello!: () => void;
  let resolve!: (value: unknown) => void;
  const request = vi.fn(
    () =>
      new Promise((done) => {
        resolve = done;
      }),
  );
  const connection = {
    id: "backend",
    host: {
      feature: () => ({
        on: (_name: string, handler: typeof changed) => {
          changed = handler;
          return () => {};
        },
        request,
      }),
    },
    onHello: (handler: () => void) => {
      hello = handler;
      return () => {};
    },
  } as unknown as HostConnection;
  const dispose = bridge.install!(connection);
  return {
    change: (value: unknown) => changed(value),
    hello: () => hello(),
    resolve: (value: unknown) => resolve(value),
    dispose,
  };
}

describe("workspace command usage", () => {
  it("ages snapshots during long-running clients and ignores history while disabled", () => {
    const connection = host();
    connection.change(snapshot);
    expect(commandUsage("backend")[0]?.weight).toBe(2);
    now = week;
    expect(commandUsage("backend")[0]?.weight).toBe(1);
    connection.change({ ...snapshot, enabled: false });
    expect(commandUsage("backend")).toEqual([]);
    expect(commandUsage("different-backend")).toEqual([]);
    connection.dispose();
  });

  it("does not let an in-flight initial snapshot overwrite a newer push or revive a closed backend", async () => {
    const connection = host();
    connection.hello();
    connection.change({ ...snapshot, entries: [] });
    connection.resolve(snapshot);
    await Promise.resolve();
    expect(commandUsage("backend")).toEqual([]);
    connection.hello();
    connection.dispose();
    connection.resolve(snapshot);
    await Promise.resolve();
    expect(commandUsage("backend")).toEqual([]);
  });

  it("separates providers while sharing Weavie command identities across pickers", () => {
    const session = (providerId: string) =>
      ({
        address: { slot: "s" },
        connection: { currentCatalog: [{ id: "s", providerId }] },
      }) as unknown as ClientSession;
    const entry = { kind: "providerCommand" as const, name: "review", commandId: null };
    expect(slashUsageId(session("one"), entry)).not.toBe(slashUsageId(session("two"), entry));
    expect(
      slashUsageId(session("one"), { ...entry, kind: "weavieCommand", commandId: "weavie.review" }),
    ).toBe("weavie.review");
    expect(slashUsageId(session("one"), { ...entry, kind: "mcpPrompt" })).toBe(
      slashUsageId(session("two"), { ...entry, kind: "mcpPrompt" }),
    );
  });
});
