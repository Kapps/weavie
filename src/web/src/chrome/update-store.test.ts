import { beforeEach, describe, expect, it, vi } from "vitest";

type HostMsg = { type: string; [k: string]: unknown };
const handlers = vi.hoisted(() => [] as Array<(m: HostMsg) => void>);
vi.mock("../bridge", () => ({
  onHostMessage: (h: (m: HostMsg) => void) => {
    handlers.push(h);
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

const deliver = (message: HostMsg): void => {
  for (const h of handlers) {
    h(message);
  }
};

describe("update-store", () => {
  beforeEach(() => {
    reload.mockClear();
    notifySpy.mockClear();
    session.clear();
    // Reset by simulating a fresh ready cycle on the same build (no restart was in flight).
    deliver({ type: "host-info", buildNumber: "0.1.100" });
    notifySpy.mockClear();
  });

  it("tracks pending holds and the restarting commit", () => {
    expect(store.updateHolds()).toBeNull();

    deliver({ type: "update-pending", holds: [{ session: "main", reason: "working" }] });
    expect(store.updateHolds()).toEqual([{ session: "main", reason: "working" }]);
    expect(store.updateRestarting()).toBe(false);

    // A session waiting on a scheduled task holds the update the same way a working one does.
    deliver({ type: "update-pending", holds: [{ session: "loop", reason: "waiting-on-task" }] });
    expect(store.updateHolds()).toEqual([{ session: "loop", reason: "waiting-on-task" }]);

    deliver({ type: "update-restarting" });
    expect(store.updateRestarting()).toBe(true);
  });

  it("clears drain state on a same-build ready cycle without a restart in flight", () => {
    deliver({ type: "update-pending", holds: [{ session: "main", reason: "shell-job" }] });

    deliver({ type: "host-info", buildNumber: "0.1.100" });
    expect(store.updateHolds()).toBeNull();
    expect(reload).not.toHaveBeenCalled();
    expect(notifySpy).not.toHaveBeenCalled();
  });

  it("warns when a restart was applying an update but the build didn't change (a rollback)", () => {
    deliver({ type: "update-restarting" });

    deliver({ type: "host-info", buildNumber: "0.1.100" });
    expect(store.updateRestarting()).toBe(false);
    expect(notifySpy).toHaveBeenCalledWith("warn", expect.stringContaining("didn't apply"));
  });

  it("reloads a stale tab, leaving the updated-to marker for the fresh page", () => {
    deliver({ type: "host-info", buildNumber: "0.1.101" });
    expect(reload).toHaveBeenCalledTimes(1);
    expect(session.get("weavie-updated-to")).toBe("0.1.101");

    // The reloaded page surfaces the notice once the toast sink exists, and consumes the marker.
    store.surfacePostUpdateNotice();
    expect(notifySpy).toHaveBeenCalledWith("info", expect.stringContaining("0.1.101"));
    store.surfacePostUpdateNotice();
    expect(notifySpy).toHaveBeenCalledTimes(1);
  });
});
