import { beforeEach, describe, expect, it, vi } from "vitest";
import type { HostConnection } from "../bridge";

type RailState = {
  lastLocation: string;
  panelPinned: boolean;
  panelWidth: number;
  parkedCollapsed: boolean;
};

const harness = vi.hoisted(() => ({
  installer: undefined as ((connection: HostConnection) => undefined | (() => void)) | undefined,
  connections: new Map<string, { connection: HostConnection; rail?: (state: RailState) => void }>(),
  posted: [] as Array<{ name: string; payload: Record<string, unknown> }>,
}));

vi.mock("../bridge", () => ({
  registerHostFeature: (installer: (connection: HostConnection) => undefined | (() => void)) => {
    harness.installer = installer;
    return () => {};
  },
  hostConnection: (id: string) => connection(id).connection,
  LOCAL_BACKEND_ID: "local",
}));

const rail = await import("./rail-state");

function connection(id: string): { connection: HostConnection; rail?: (state: RailState) => void } {
  const existing = harness.connections.get(id);
  if (existing !== undefined) {
    return existing;
  }
  const created = {} as { connection: HostConnection; rail?: (state: RailState) => void };
  created.connection = {
    id,
    isLocal: id === "local",
    onHello: () => () => {},
    host: {
      feature: () => ({
        on: (_name: string, handler: (state: RailState) => void) => {
          created.rail = handler;
          return () => {};
        },
        publish: (name: string, payload: Record<string, unknown>) => {
          harness.posted.push({ name, payload });
        },
      }),
    },
  } as unknown as HostConnection;
  harness.connections.set(id, created);
  harness.installer?.(created.connection);
  return created;
}

const state = (patch: Partial<RailState>): RailState => ({
  lastLocation: "local",
  panelPinned: false,
  panelWidth: 300,
  parkedCollapsed: true,
  ...patch,
});

beforeEach(() => {
  connection("local").rail?.(state({}));
  harness.posted.length = 0;
});

describe("rail-state host sync", () => {
  it("adopts the local rail-state push", () => {
    connection("local").rail?.(
      state({
        lastLocation: "remote:r",
        panelPinned: true,
        panelWidth: 240,
        parkedCollapsed: false,
      }),
    );
    expect(rail.lastLocation()).toBe("remote:r");
    expect(rail.panelPinned()).toBe(true);
    expect(rail.panelWidth()).toBe(240);
    expect(rail.parkedCollapsed()).toBe(false);
  });

  it("does not install the local rail-state channel on a remote backend", () =>
    expect(connection("remote:r").rail).toBeUndefined());
});

describe("setters", () => {
  it("update the signal and tell the local backend", () => {
    rail.setLastLocation("remote:z");
    rail.setParkedCollapsed(false);
    expect(rail.lastLocation()).toBe("remote:z");
    expect(rail.parkedCollapsed()).toBe(false);
    expect(harness.posted).toEqual([
      { name: "setLastLocation", payload: { location: "remote:z" } },
      { name: "setParkedCollapsed", payload: { collapsed: false } },
    ]);
  });
});
