import { beforeEach, describe, expect, it, vi } from "vitest";
import type { HostConnection } from "../bridge";
import type { HostHello } from "../messaging/host-connection";

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

const handlers = vi.hoisted(() => ({
  pending: undefined as ((payload: { holds: unknown[] }) => void) | undefined,
  restarting: undefined as (() => void) | undefined,
  hello: undefined as ((hello: HostHello) => void) | undefined,
}));
vi.mock("../bridge", () => ({
  activeBackendId: () => "local",
  registerHostFeature: (installer: (connection: HostConnection) => undefined | (() => void)) => {
    installer({
      id: "local",
      isLocal: true,
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

const deliverPending = (holds: unknown[]): void => handlers.pending?.({ holds });
const deliverRestarting = (): void => handlers.restarting?.();
const deliverHello = (buildNumber: string): void => handlers.hello?.({ buildNumber } as HostHello);

describe("update-store", () => {
  beforeEach(() => {
    session.clear();
    // Clean slate: a restart-in-flight returning on the same build (a rollback) clears the holds, the
    // restarting flag, AND the episode-pending latch — the one message path that resets all three.
    deliverRestarting();
    deliverHello("0.1.100");
    reload.mockClear();
    notifySpy.mockClear();
  });

  it("tracks pending holds and the restarting commit", () => {
    expect(store.updateHolds()).toBeNull();

    deliverPending([{ session: "main", reason: "working" }]);
    expect(store.updateHolds()).toEqual([{ session: "main", reason: "working" }]);
    expect(store.updateRestarting()).toBe(false);

    // A session waiting on a scheduled task holds the update the same way a working one does.
    deliverPending([{ session: "loop", reason: "waiting-on-task" }]);
    expect(store.updateHolds()).toEqual([{ session: "loop", reason: "waiting-on-task" }]);

    deliverRestarting();
    expect(store.updateRestarting()).toBe(true);
  });

  it("announces once when an update first stages, then refreshes holds silently", () => {
    deliverPending([{ session: "main", reason: "working" }]);
    expect(notifySpy).toHaveBeenCalledTimes(1);
    expect(notifySpy).toHaveBeenCalledWith(
      "info",
      expect.stringContaining("Update ready"),
      expect.any(String),
    );

    // A changed hold set while still pending must not re-toast.
    deliverPending([{ session: "loop", reason: "waiting-on-task" }]);
    expect(notifySpy).toHaveBeenCalledTimes(1);
  });

  it("announces only once across a mid-drain reconnect (host-info transiently clears holds)", () => {
    deliverPending([{ session: "main", reason: "working" }]);
    expect(notifySpy).toHaveBeenCalledTimes(1);

    // A reconnect: the host answers `ready` with host-info (nulling holds) then re-pushes the pending
    // state. The episode latch must survive that transient clear so the re-push does not re-announce.
    deliverHello("0.1.100");
    expect(store.updateHolds()).toBeNull();
    deliverPending([{ session: "main", reason: "working" }]);
    expect(store.updatePending()).toBe(true);
    expect(notifySpy).toHaveBeenCalledTimes(1);
  });

  it("clears drain state on a same-build ready cycle without a restart in flight", () => {
    deliverPending([{ session: "main", reason: "shell-job" }]);
    notifySpy.mockClear(); // ignore the first-pending toast; this asserts the clear path itself is silent

    deliverHello("0.1.100");
    expect(store.updateHolds()).toBeNull();
    expect(reload).not.toHaveBeenCalled();
    expect(notifySpy).not.toHaveBeenCalled();
  });

  it("warns when a restart was applying an update but the build didn't change (a rollback)", () => {
    deliverRestarting();

    deliverHello("0.1.100");
    expect(store.updateRestarting()).toBe(false);
    expect(notifySpy).toHaveBeenCalledWith("warn", expect.stringContaining("didn't apply"));
  });

  it("reloads a stale tab, leaving the updated-to marker for the fresh page", () => {
    deliverHello("0.1.101");
    expect(reload).toHaveBeenCalledTimes(1);
    expect(session.get("weavie-updated-to")).toBe("0.1.101");

    // The reloaded page surfaces the notice once the toast sink exists, and consumes the marker.
    store.surfacePostUpdateNotice();
    expect(notifySpy).toHaveBeenCalledWith("info", expect.stringContaining("0.1.101"));
    store.surfacePostUpdateNotice();
    expect(notifySpy).toHaveBeenCalledTimes(1);
  });
});
