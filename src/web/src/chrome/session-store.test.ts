import { createRoot, createSignal } from "solid-js";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession, HostConnection } from "../bridge";
import type { SessionCatalogEntry } from "../messaging/host-connection";
import type { RailSession } from "./session-store";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

// NOTE: the store's working-set views (`sessions`, `railSessions`, `remoteAgentRows`) are module-scope Solid
// memos. Outside a render root they never track their sources, so they can't be exercised in this pure-node
// env — that reactive behaviour is covered by the Playwright e2e suite (e2e/functional/session.spec.ts).
// `claudeStatus` is a plain signal, so its host-sync gating IS unit-testable here.

const harness = vi.hoisted(() => ({
  hostInstallers: [] as Array<(connection: HostConnection) => undefined | (() => void)>,
  sessionInstallers: [] as Array<(session: ClientSession) => undefined | (() => void)>,
  connections: new Map<
    string,
    {
      connection: HostConnection;
      catalogs: Array<(catalog: SessionCatalogEntry[]) => void>;
    }
  >(),
  sessions: new Map<
    string,
    {
      client: ClientSession;
      status?: (payload: { status: string }) => void;
    }
  >(),
  selectionListeners: [] as Array<(session: ClientSession | null) => void>,
  setSelected: (_session: ClientSession | null): void => {},
}));

vi.mock("../bridge", () => {
  const [selectedSession, setSelected] = createSignal<ClientSession | null>(null);
  harness.setSelected = (session) => {
    setSelected(session);
    for (const listener of harness.selectionListeners) {
      listener(session);
    }
  };
  return {
    backendName: (id: string) => id,
    backendPhase: () => "online",
    connectBackend: () => {},
    connectedBackends: () =>
      [...harness.connections.keys()].map((id) => ({
        id,
        name: id,
        isLocal: id === "local",
      })),
    disconnectBackend: () => {},
    hostConnection: (id: string) => connection(id).connection,
    log: () => {},
    onBackendDisconnected: () => () => {},
    onBackendPhase: () => () => {},
    onSelectedSession: (listener: (session: ClientSession | null) => void) => {
      harness.selectionListeners.push(listener);
      listener(selectedSession());
      return () => {};
    },
    registerHostFeature: (installer: (connection: HostConnection) => undefined | (() => void)) => {
      harness.hostInstallers.push(installer);
      return () => {};
    },
    registerSessionFeature: (installer: (session: ClientSession) => undefined | (() => void)) => {
      harness.sessionInstallers.push(installer);
      return () => {};
    },
    selectedSession,
    LOCAL_BACKEND_ID: "local",
  };
});

const store = await import("./session-store");

function connection(backendId: string): {
  connection: HostConnection;
  catalogs: Array<(catalog: SessionCatalogEntry[]) => void>;
} {
  const existing = harness.connections.get(backendId);
  if (existing !== undefined) {
    return existing;
  }
  const catalogs: Array<(catalog: SessionCatalogEntry[]) => void> = [];
  const created = {
    connection: {
      id: backendId,
      isLocal: backendId === "local",
      onCatalog: (handler: (catalog: SessionCatalogEntry[]) => void) => {
        catalogs.push(handler);
        return () => {};
      },
      session: (address: { slot: string }) =>
        harness.sessions.get(`${backendId}\u0000${address.slot}`)?.client,
      onHello: () => () => {},
      host: {
        feature: () => ({
          on: () => () => {},
          publish: () => {},
        }),
      },
    } as unknown as HostConnection,
    catalogs,
  };
  harness.connections.set(backendId, created);
  for (const installer of harness.hostInstallers) {
    installer(created.connection);
  }
  return created;
}

function session(
  backendId: string,
  slot: string,
): {
  client: ClientSession;
  status?: (payload: { status: string }) => void;
} {
  const key = `${backendId}\u0000${slot}`;
  const existing = harness.sessions.get(key);
  if (existing !== undefined) {
    return existing;
  }
  const created = {} as {
    client: ClientSession;
    status?: (payload: { status: string }) => void;
  };
  created.client = {
    connection: connection(backendId).connection,
    address: { slot, incarnation: `${slot}-incarnation` },
    feature: (feature: string) => ({
      on: (_name: string, handler: (payload: { status: string }) => void) => {
        if (feature === "status") {
          created.status = handler;
        }
        return () => {};
      },
    }),
  } as unknown as ClientSession;
  harness.sessions.set(key, created);
  for (const installer of harness.sessionInstallers) {
    installer(created.client);
  }
  return created;
}

function deliverCatalog(backendId: string, slots: string[]): void {
  const catalog = slots.map(
    (slot) =>
      ({
        id: slot,
        label: slot,
        address: { slot, incarnation: `${slot}-incarnation` },
        loaded: true,
        providerId: "claude",
        status: "starting",
        hue: 0,
        monogram: slot.slice(0, 1),
      }) as SessionCatalogEntry,
  );
  for (const handler of connection(backendId).catalogs) {
    handler(catalog);
  }
}

const deliverStatus = (backendId: string, slot: string, status: string): void =>
  session(backendId, slot).status?.({ status });

beforeEach(() => {
  const primary = session("local", "main");
  harness.setSelected(primary.client);
  deliverCatalog("local", ["main"]);
  deliverStatus("local", "main", "starting");
});

describe("selected session status", () => {
  it("adopts the selected session's status", () => {
    deliverStatus("local", "main", "working");
    expect(store.claudeStatus()).toBe("working");
  });

  it("retains background status without leaking it into the selected session", () => {
    deliverStatus("local", "main", "working");
    session("remote:r", "feature");
    deliverCatalog("remote:r", ["feature"]);
    deliverStatus("remote:r", "feature", "idle");
    expect(store.claudeStatus()).toBe("working");
  });

  it("adopts the waiting status (idle but resuming on a scheduled task)", () => {
    deliverStatus("local", "main", "waiting");
    expect(store.claudeStatus()).toBe("waiting");
  });
});

const chip = (id: string, active: boolean): RailSession => ({
  owner: null,
  id,
  label: id,
  active,
  loaded: true,
  providerId: "claude",
  agentSurface: "terminal",
  agentInputProtocol: 0,
  status: "idle",
  hue: 0,
  monogram: id.slice(0, 1),
  backendId: "local",
  locationName: "default",
  isLocal: true,
  pending: false,
  offline: false,
});

describe("stepRailTarget", () => {
  it("returns null when there's nothing to move to", () => {
    expect(store.stepRailTarget([], 1)).toBeNull();
    expect(store.stepRailTarget([], -1)).toBeNull();
    // A lone active chip has no sibling to cycle to, so the keystroke falls through.
    expect(store.stepRailTarget([chip("a", true)], 1)).toBeNull();
    expect(store.stepRailTarget([chip("a", true)], -1)).toBeNull();
  });

  it("steps to the next/prev chip from the active one, wrapping the ends", () => {
    const list = [chip("a", false), chip("b", true), chip("c", false)];
    expect(store.stepRailTarget(list, 1)?.id).toBe("c");
    expect(store.stepRailTarget(list, -1)?.id).toBe("a");
    expect(store.stepRailTarget([chip("a", true), chip("b", false)], -1)?.id).toBe("b");
    expect(store.stepRailTarget([chip("a", false), chip("b", true)], 1)?.id).toBe("a");
  });

  // The regression: after deleting the focused session the page can be left with no active rail chip. Cycling
  // must then recover focus to the near end (first for next, last for prev) rather than dead-key.
  it("recovers focus to the near end when no chip is active", () => {
    expect(store.stepRailTarget([chip("a", false)], 1)?.id).toBe("a");
    expect(store.stepRailTarget([chip("a", false)], -1)?.id).toBe("a");
    const list = [chip("a", false), chip("b", false), chip("c", false)];
    expect(store.stepRailTarget(list, 1)?.id).toBe("a");
    expect(store.stepRailTarget(list, -1)?.id).toBe("c");
  });

  // The double-press: with the highlight off the cycle list (a switch to a dormant chip mid-flight), recovering
  // to the near end handed back the session already on screen, so the first Ctrl+Tab only repaired the highlight.
  it("skips the session already on screen when recovering", () => {
    const onScreen = { ...chip("main", false), owner: session("local", "main").client };
    expect(store.stepRailTarget([onScreen, chip("b", false), chip("c", false)], 1)?.id).toBe("b");
    expect(store.stepRailTarget([chip("b", false), chip("c", false), onScreen], -1)?.id).toBe("c");
    expect(store.stepRailTarget([onScreen], 1)).toBeNull();
    expect(store.stepRailTarget([onScreen], -1)).toBeNull();
  });
});

describe("beginSessionSelection", () => {
  it("drops the optimistic highlight when its switch settles, leaving a newer one alone", () => {
    createRoot((dispose) => {
      deliverCatalog("local", ["main", "second", "third"]);
      const endSecond = store.beginSessionSelection("local", "second");
      expect(store.railSessions().find((s) => s.active)?.id).toBe("second");

      const endThird = store.beginSessionSelection("local", "third");
      endSecond();
      expect(store.railSessions().find((s) => s.active)?.id).toBe("third");

      endThird();
      expect(store.railSessions().find((s) => s.active)?.id).toBe("main");
      dispose();
    });
  });
});
