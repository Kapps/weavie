import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession, HostConnection } from "../bridge";

const bridge = vi.hoisted(() => ({
  publish: vi.fn(),
  install: null as null | ((connection: HostConnection) => () => void),
}));
vi.mock("../bridge", () => ({
  registerHostFeature: (install: typeof bridge.install) => {
    bridge.install = install;
  },
  hostConnection: () => ({ host: { feature: () => ({ publish: bridge.publish }) } }),
}));
const { captureCommandUsage, commandUsage, slashUsageId } = await import("./usage-store");

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
  let hello!: (value: { commandUsage: unknown }) => void;
  const connection = {
    id: "backend",
    host: {
      feature: () => ({
        on: (_name: string, handler: typeof changed) => {
          changed = handler;
          return () => {};
        },
      }),
    },
    onHello: (handler: typeof hello) => {
      hello = handler;
      return () => {};
    },
  } as unknown as HostConnection;
  const dispose = bridge.install!(connection);
  return {
    change: (value: unknown) => changed(value),
    hello: (value: unknown) => hello({ commandUsage: value }),
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

  it("replaces history on reconnect and clears it when the backend is removed", () => {
    const connection = host();
    connection.hello(snapshot);
    expect(commandUsage("backend")[0]?.weight).toBe(2);
    connection.change({ ...snapshot, entries: [] });
    expect(commandUsage("backend")).toEqual([]);
    connection.hello(snapshot);
    expect(commandUsage("backend")[0]?.weight).toBe(2);
    connection.dispose();
    expect(commandUsage("backend")).toEqual([]);
  });

  it("never learns a choice made while disabled even if execution finishes after re-enabling", () => {
    const connection = host();
    bridge.publish.mockClear();
    connection.hello({ ...snapshot, enabled: false });
    const disabledChoice = captureCommandUsage("backend", "review", "rev");
    connection.change(snapshot);
    disabledChoice();
    expect(bridge.publish).not.toHaveBeenCalled();
    const enabledChoice = captureCommandUsage("backend", "review", "rev");
    enabledChoice();
    expect(bridge.publish).toHaveBeenCalledExactlyOnceWith("record", {
      id: "review",
      query: "rev",
    });
    connection.dispose();
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
