import { beforeEach, describe, expect, it, vi } from "vitest";
import type { HostConnection } from "../bridge";
import type { HostHello } from "../messaging/host-connection";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

const bridgeHarness = vi.hoisted(() => ({
  activeBackendId: "local",
  installer: undefined as ((connection: HostConnection) => undefined | (() => void)) | undefined,
}));
vi.mock("../bridge", () => ({
  activeBackendId: () => bridgeHarness.activeBackendId,
  registerHostFeature: (installer: (connection: HostConnection) => undefined | (() => void)) => {
    bridgeHarness.installer = installer;
    return () => {};
  },
}));

const notifySpy = vi.hoisted(() => vi.fn());
vi.mock("../notify/notify", () => ({ notify: notifySpy }));

const reload = vi.fn();
const session = new Map<string, string>();
vi.stubGlobal("window", {
  __WEAVIE_SHELL__: { buildNumber: "0.1.100" },
  location: { reload },
  sessionStorage: {
    getItem: (k: string) => session.get(k) ?? null,
    setItem: (k: string, v: string) => session.set(k, v),
    removeItem: (k: string) => session.delete(k),
  },
});

const store = await import("./update-store");

interface ConnectionHandlers {
  id: string;
  pending: ((payload: { holds: unknown[] }) => void) | undefined;
  restarting: (() => void) | undefined;
  hello: ((hello: HostHello) => void) | undefined;
}

function installConnection(id: string, isLocal: boolean): ConnectionHandlers {
  const handlers: ConnectionHandlers = {
    id,
    pending: undefined,
    restarting: undefined,
    hello: undefined,
  };
  const installer = bridgeHarness.installer;
  if (installer === undefined) {
    throw new Error("update-store did not register its host feature");
  }
  installer({
    id,
    isLocal,
    host: {
      feature: () => ({
        on: (name: string, handler: (payload: { holds: unknown[] }) => void) => {
          if (name === "pending") {
            handlers.pending = handler;
          } else {
            handlers.restarting = handler as () => void;
          }
          return () => {};
        },
      }),
    },
    onHello: (handler: (hello: HostHello) => void) => {
      handlers.hello = handler;
      return () => {};
    },
  } as unknown as HostConnection);
  return handlers;
}

const local = installConnection("local", true);
const remote = installConnection("remote:devbox", false);

function deliverPending(connection: ConnectionHandlers, holds: unknown[]): void {
  connection.pending?.({ holds });
}

function deliverRestarting(connection: ConnectionHandlers): void {
  connection.restarting?.();
}

function deliverHello(connection: ConnectionHandlers, buildNumber: string): void {
  connection.hello?.({ buildNumber } as HostHello);
}

describe("update-store", () => {
  beforeEach(() => {
    bridgeHarness.activeBackendId = local.id;
    session.clear();
    deliverHello(local, "0.1.100");
    deliverRestarting(local);
    deliverHello(local, "0.1.100");
    deliverHello(remote, "0.1.200");
    deliverRestarting(remote);
    deliverHello(remote, "0.1.200");
    reload.mockClear();
    notifySpy.mockClear();
  });

  it("tracks pending holds and the restarting commit", () => {
    expect(store.updateHolds()).toBeNull();

    deliverPending(local, [{ session: "main", reason: "working" }]);
    expect(store.updateHolds()).toEqual([{ session: "main", reason: "working" }]);
    expect(store.updateRestarting()).toBe(false);

    // A session waiting on a scheduled task holds the update the same way a working one does.
    deliverPending(local, [{ session: "loop", reason: "waiting-on-task" }]);
    expect(store.updateHolds()).toEqual([{ session: "loop", reason: "waiting-on-task" }]);

    deliverRestarting(local);
    expect(store.updateRestarting()).toBe(true);
  });

  it("announces once when an update first stages, then refreshes holds silently", () => {
    deliverPending(local, [{ session: "main", reason: "working" }]);
    expect(notifySpy).toHaveBeenCalledTimes(1);
    expect(notifySpy).toHaveBeenCalledWith(
      "info",
      expect.stringContaining("Update ready"),
      expect.any(String),
    );

    // A changed hold set while still pending must not re-toast.
    deliverPending(local, [{ session: "loop", reason: "waiting-on-task" }]);
    expect(notifySpy).toHaveBeenCalledTimes(1);
  });

  it("announces only once across a mid-drain reconnect (host-info transiently clears holds)", () => {
    deliverPending(local, [{ session: "main", reason: "working" }]);
    expect(notifySpy).toHaveBeenCalledTimes(1);

    // A reconnect hello clears holds before the host re-pushes the pending
    // state. The episode latch must survive that transient clear so the re-push does not re-announce.
    deliverHello(local, "0.1.100");
    expect(store.updateHolds()).toBeNull();
    deliverPending(local, [{ session: "main", reason: "working" }]);
    expect(store.updatePending()).toBe(true);
    expect(notifySpy).toHaveBeenCalledTimes(1);
  });

  it("clears drain state on a same-build ready cycle without a restart in flight", () => {
    deliverPending(local, [{ session: "main", reason: "shell-job" }]);
    notifySpy.mockClear(); // ignore the first-pending toast; this asserts the clear path itself is silent

    deliverHello(local, "0.1.100");
    expect(store.updateHolds()).toBeNull();
    expect(reload).not.toHaveBeenCalled();
    expect(notifySpy).not.toHaveBeenCalled();
  });

  it("warns when a restart was applying an update but the build didn't change (a rollback)", () => {
    deliverRestarting(local);

    deliverHello(local, "0.1.100");
    expect(store.updateRestarting()).toBe(false);
    expect(notifySpy).toHaveBeenCalledWith("warn", expect.stringContaining("didn't apply"));
  });

  it("recognizes a changed remote build as a successful update without reloading", () => {
    bridgeHarness.activeBackendId = remote.id;
    deliverPending(remote, [{ session: "remote", reason: "working" }]);
    deliverRestarting(remote);
    notifySpy.mockClear();

    deliverHello(remote, "0.1.201");

    expect(reload).not.toHaveBeenCalled();
    expect(store.updateHolds()).toBeNull();
    expect(store.updatePending()).toBe(false);
    expect(store.updateRestarting()).toBe(false);
    expect(notifySpy).toHaveBeenCalledWith("info", "Weavie updated to build 0.1.201.");
    expect(notifySpy).not.toHaveBeenCalledWith("warn", expect.any(String));
  });

  it("warns when a remote worker returns on its previous build", () => {
    bridgeHarness.activeBackendId = remote.id;
    deliverRestarting(remote);

    deliverHello(remote, "0.1.200");

    expect(reload).not.toHaveBeenCalled();
    expect(store.updateRestarting()).toBe(false);
    expect(notifySpy).toHaveBeenCalledWith("warn", expect.stringContaining("didn't apply"));
  });

  it("keeps update state isolated by backend", () => {
    deliverPending(local, [{ session: "local", reason: "shell-job" }]);
    expect(store.updateHolds()).toEqual([{ session: "local", reason: "shell-job" }]);

    bridgeHarness.activeBackendId = remote.id;
    deliverPending(remote, [{ session: "remote", reason: "needs-input" }]);
    expect(store.updateHolds()).toEqual([{ session: "remote", reason: "needs-input" }]);
  });

  it("reloads a stale tab, leaving the updated-to marker for the fresh page", () => {
    deliverHello(local, "0.1.101");
    expect(reload).toHaveBeenCalledTimes(1);
    expect(session.get("weavie-updated-to")).toBe("0.1.101");

    // The reloaded page surfaces the notice once the toast sink exists, and consumes the marker.
    store.surfacePostUpdateNotice();
    expect(notifySpy).toHaveBeenCalledWith("info", expect.stringContaining("0.1.101"));
    store.surfacePostUpdateNotice();
    expect(notifySpy).toHaveBeenCalledTimes(1);
  });
});
