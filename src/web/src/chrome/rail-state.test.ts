import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession, HostConnection } from "../bridge";
import type { SessionCatalogEntry } from "../messaging/host-connection";

const harness = vi.hoisted(() => ({
  installer: undefined as ((connection: HostConnection) => undefined | (() => void)) | undefined,
  connections: new Map<
    string,
    {
      connection: HostConnection;
      catalog?: (catalog: SessionCatalogEntry[]) => void;
      rail?: (state: { lastLocation: string; promoted: string[] }) => void;
    }
  >(),
  selected: null as ClientSession | null,
  posted: [] as Array<{ name: string; payload: Record<string, unknown> }>,
}));

vi.mock("../bridge", () => ({
  registerHostFeature: (installer: (connection: HostConnection) => undefined | (() => void)) => {
    harness.installer = installer;
    return () => {};
  },
  hostConnection: (id: string) => connection(id).connection,
  selectedSession: () => harness.selected,
  LOCAL_BACKEND_ID: "local",
}));

const rail = await import("./rail-state");

function connection(id: string): {
  connection: HostConnection;
  catalog?: (catalog: SessionCatalogEntry[]) => void;
  rail?: (state: { lastLocation: string; promoted: string[] }) => void;
} {
  const existing = harness.connections.get(id);
  if (existing !== undefined) {
    return existing;
  }
  const created = {} as {
    connection: HostConnection;
    catalog?: (catalog: SessionCatalogEntry[]) => void;
    rail?: (state: { lastLocation: string; promoted: string[] }) => void;
  };
  created.connection = {
    id,
    isLocal: id === "local",
    onCatalog: (handler: (catalog: SessionCatalogEntry[]) => void) => {
      created.catalog = handler;
      return () => {};
    },
    onHello: () => () => {},
    host: {
      feature: () => ({
        on: (
          _name: string,
          handler: (state: { lastLocation: string; promoted: string[] }) => void,
        ) => {
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

const deliverRail = (state: { lastLocation: string; promoted: string[] }): void => {
  connection("local").rail?.(state);
};

const deliverCatalog = (backendId: string, catalog: SessionCatalogEntry[]): void => {
  connection(backendId).catalog?.(catalog);
};

const chip = (id: string): SessionCatalogEntry =>
  ({
    id,
    address: { slot: id, incarnation: `${id}-incarnation` },
  }) as SessionCatalogEntry;

beforeEach(() => {
  harness.posted.length = 0;
  harness.selected = null;
  deliverRail({ lastLocation: "local", promoted: [] });
  harness.posted.length = 0;
});

describe("rail-state host sync", () => {
  it("adopts lastLocation + promoted from a local rail-state push", () => {
    deliverRail({ lastLocation: "remote:r", promoted: ["remote:r s1"] });
    expect(rail.lastLocation()).toBe("remote:r");
    expect(rail.isPromoted("remote:r", "s1")).toBe(true);
  });

  it("does not install the local rail-state channel on a remote backend", () =>
    expect(connection("remote:r").rail).toBeUndefined());
});

describe("promote / demote", () => {
  it("promotes a remote session and pushes the new set to local", () => {
    rail.promoteSession("remote:a", "s1");
    expect(rail.isPromoted("remote:a", "s1")).toBe(true);
    expect(harness.posted).toContainEqual({
      name: "setPromoted",
      payload: { promoted: ["remote:a s1"] },
    });
  });

  it("is idempotent: re-promoting pushes nothing new", () => {
    rail.promoteSession("remote:a", "s1");
    harness.posted.length = 0;
    rail.promoteSession("remote:a", "s1");
    expect(harness.posted).toEqual([]);
  });

  it("demotes a promoted session and pushes the shrunk set", () => {
    rail.promoteSession("remote:a", "s1");
    harness.posted.length = 0;
    rail.demoteSession("remote:a", "s1");
    expect(rail.isPromoted("remote:a", "s1")).toBe(false);
    expect(harness.posted).toContainEqual({
      name: "setPromoted",
      payload: { promoted: [] },
    });
  });

  it("demoting a non-promoted session is a no-op", () => {
    rail.demoteSession("remote:a", "ghost");
    expect(harness.posted).toEqual([]);
  });
});

describe("setLastLocation", () => {
  it("updates the signal and tells the local backend", () => {
    rail.setLastLocation("remote:z");
    expect(rail.lastLocation()).toBe("remote:z");
    expect(harness.posted).toContainEqual({
      name: "setLastLocation",
      payload: { location: "remote:z" },
    });
  });
});

describe("promoteNextSessionOn (one-shot auto-promote)", () => {
  it("promotes the genuinely new session in the next catalog, preferring the selected one", () => {
    // A prior list establishes the known ids on this backend.
    deliverCatalog("remote:n1", [chip("a")]);
    rail.promoteNextSessionOn("remote:n1");
    const remote = connection("remote:n1").connection;
    harness.selected = {
      connection: remote,
      address: { slot: "b", incarnation: "b-incarnation" },
    } as ClientSession;
    deliverCatalog("remote:n1", [chip("a"), chip("b")]);
    expect(rail.isPromoted("remote:n1", "b")).toBe(true);
    expect(rail.isPromoted("remote:n1", "a")).toBe(false);
  });

  it("waits for a list that actually contains a new id rather than consuming the one-shot early", () => {
    deliverCatalog("remote:n2", [chip("a")]);
    rail.promoteNextSessionOn("remote:n2");
    deliverCatalog("remote:n2", [chip("a")]);
    deliverCatalog("remote:n2", [chip("a"), chip("b")]);
    expect(rail.isPromoted("remote:n2", "b")).toBe(true);
  });

  it("is one-shot: a later new session is not auto-promoted", () => {
    rail.promoteNextSessionOn("remote:n3");
    deliverCatalog("remote:n3", [chip("b")]);
    expect(rail.isPromoted("remote:n3", "b")).toBe(true);
    deliverCatalog("remote:n3", [chip("b"), chip("c")]);
    expect(rail.isPromoted("remote:n3", "c")).toBe(false);
  });

  it("never arms for the local backend", () => {
    rail.promoteNextSessionOn("local");
    deliverCatalog("local", [chip("new")]);
    expect(rail.isPromoted("local", "new")).toBe(false);
  });
});
