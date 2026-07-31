import { createComputed, createRoot, createSignal } from "solid-js";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession, HostConnection } from "../bridge";
import type { HostHello } from "../messaging/host-connection";
import type { LayoutDocument } from "./types";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

const posted = vi.hoisted(
  () =>
    [] as Array<{
      backendId: string;
      feature: string;
      name: string;
      payload: Record<string, unknown>;
    }>,
);
const bridgeState = vi.hoisted(
  () =>
    ({
      installer: undefined as
        | ((connection: HostConnection) => undefined | (() => void))
        | undefined,
      connections: new Map(),
      setActiveBackendId: (_backendId: string): void => {},
    }) as {
      installer: ((connection: HostConnection) => undefined | (() => void)) | undefined;
      connections: Map<
        string,
        {
          connection: HostConnection;
          hello?: (hello: HostHello) => void;
          state?: (payload: { document: LayoutDocument }) => void;
        }
      >;
      setActiveBackendId: (backendId: string) => void;
    },
);

vi.mock("../bridge", () => {
  const [selected, setSelected] = createSignal<ClientSession | null>(null);
  bridgeState.setActiveBackendId = (backendId: string) =>
    setSelected({
      connection: { id: backendId },
      address: { slot: "primary", incarnation: `${backendId}-primary` },
    } as ClientSession);
  return {
    selectedSession: selected,
    registerHostFeature: (installer: (connection: HostConnection) => undefined | (() => void)) => {
      bridgeState.installer = installer;
      return () => {};
    },
    hostConnection: (backendId: string) => connection(backendId).connection,
  };
});

const store = await import("./store");

function connection(backendId: string): {
  connection: HostConnection;
  hello?: (hello: HostHello) => void;
  state?: (payload: { document: LayoutDocument }) => void;
} {
  const existing = bridgeState.connections.get(backendId);
  if (existing !== undefined) {
    return existing;
  }
  const created = {} as {
    connection: HostConnection;
    hello?: (hello: HostHello) => void;
    state?: (payload: { document: LayoutDocument }) => void;
  };
  created.connection = {
    id: backendId,
    onHello: (handler: (hello: HostHello) => void) => {
      created.hello = handler;
      return () => {};
    },
    host: {
      feature: (feature: string) => ({
        on: (_name: string, handler: (payload: { document: LayoutDocument }) => void) => {
          created.state = handler;
          return () => {};
        },
        publish: (name: string, payload: Record<string, unknown>) => {
          posted.push({ backendId, feature, name, payload });
        },
      }),
    },
  } as unknown as HostConnection;
  bridgeState.connections.set(backendId, created);
  bridgeState.installer?.(created.connection);
  return created;
}

const deliver = (backendId: string, value: LayoutDocument): void => {
  connection(backendId).state?.({ document: value });
};

const document = (top: number): LayoutDocument => ({
  version: 1,
  seenPaneLevel: 1,
  focused: "p_claude",
  dismissed: [],
  root: {
    type: "split",
    dir: "column",
    weights: [top, 1 - top],
    children: [
      { type: "pane", id: "p_claude", kind: "terminal:claude" },
      { type: "pane", id: "p_shell", kind: "terminal:shell" },
    ],
  },
});

beforeEach(() => {
  posted.length = 0;
  bridgeState.setActiveBackendId("local");
});

describe("layout host ownership", () => {
  it("restores the cached document when its backend becomes active", () => {
    const local = document(0.75);
    const remote = document(0.25);
    deliver("remote:test", remote);
    deliver("local", local);

    expect(store.layoutDocument()).toEqual(local);
    bridgeState.setActiveBackendId("remote:test");
    expect(store.layoutDocument()).toEqual(remote);
  });

  it("does not notify the active layout when a background backend restores", () => {
    const local = document(0.7);
    deliver("local", local);
    let notifications = 0;
    const dispose = createRoot((rootDispose) => {
      createComputed(() => {
        store.layoutDocument();
        notifications += 1;
      });
      return rootDispose;
    });

    deliver("remote:test", document(0.2));

    expect(notifications).toBe(1);
    dispose();
  });

  it("sends layout changes to the backend that owned the gesture", () => {
    const changed = document(0.6);
    store.sendLayout("remote:test", changed);

    expect(posted).toEqual([
      {
        backendId: "remote:test",
        feature: "layout",
        name: "changed",
        payload: { document: changed },
      },
    ]);
  });
});
