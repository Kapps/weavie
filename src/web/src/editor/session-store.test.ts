import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { EditorSessionEntry } from "./session-types";

// Capture the store's host listener + every outbound message; the bridge itself is window-coupled.
const posted = vi.hoisted(() => [] as Array<Record<string, unknown>>);
const hostHandlers = vi.hoisted(() => [] as Array<(m: unknown, backendId: string) => void>);
const postedBackends = vi.hoisted(() => [] as Array<string | null>);
const bridgeState = vi.hoisted(() => ({
  activeBackendId: "local",
  binding: null as {
    backendId: string;
    protocol: "projection";
    sessionId: string | null;
    railSessionId: string;
    projectionEpoch: string;
    projectionRevision: number;
    projectionPageId: string;
  } | null,
}));
vi.mock("../bridge", () => ({
  currentEditorBinding: () => bridgeState.binding,
  editorBackendId: () => bridgeState.binding?.backendId ?? null,
  editorSessionId: () => bridgeState.binding?.sessionId ?? null,
  onHostMessage: (h: (m: unknown, backendId: string) => void) => {
    hostHandlers.push(h);
    return () => {};
  },
  editorAttribution: (binding: NonNullable<typeof bridgeState.binding>) => ({
    sessionId: binding.sessionId,
    projectionEpoch: binding.projectionEpoch,
    projectionRevision: binding.projectionRevision,
    projectionPageId: binding.projectionPageId,
  }),
  postToEditorBinding: (
    binding: NonNullable<typeof bridgeState.binding>,
    m: Record<string, unknown>,
  ) => {
    posted.push(m);
    postedBackends.push(binding.backendId);
  },
}));

const store = await import("./session-store");

type Entry = EditorSessionEntry;

// Seed the store via the host's set-editor-session, then clear the captured traffic so each test starts fresh.
function seed(open: Entry[], active: string | null, owner = "sess-1", backendId = "local"): void {
  bridgeState.binding = {
    backendId,
    protocol: "projection",
    sessionId: owner,
    railSessionId: owner,
    projectionEpoch: "host-test",
    projectionRevision: 1,
    projectionPageId: "page-test",
  };
  for (const h of hostHandlers) {
    h({ type: "set-editor-session", sessionId: owner, session: { active, open } }, backendId);
  }
  posted.length = 0;
  postedBackends.length = 0;
}

const openEditorsPushes = (): Array<Record<string, unknown>> =>
  posted.filter((m) => m.type === "open-editors-changed");
const paths = (): string[] => store.openTabs().map((e) => e.path);

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  vi.runOnlyPendingTimers();
  vi.useRealTimers();
});

describe("openTab", () => {
  it("opens a fresh persistent tab, activates it, and pushes the new tab set", () => {
    seed([], null);
    const res = store.openTab("/a.ts", { line: 5 });
    expect(res).toEqual({ path: "/a.ts", placement: { line: 5 } });
    expect(store.activePath()).toBe("/a.ts");
    expect(openEditorsPushes()).toHaveLength(1);
  });

  it("reuses the single preview slot instead of stacking preview tabs", () => {
    seed([], null);
    store.openTab("/p1.ts", { preview: true });
    store.openTab("/p2.ts", { preview: true });
    expect(paths()).toEqual(["/p2.ts"]);
    expect(store.openTabs()[0]?.preview).toBe(true);
  });

  it("promotes a preview tab to persistent on a non-preview open", () => {
    seed([{ path: "/p.ts", viewState: null, preview: true }], "/p.ts");
    store.openTab("/p.ts");
    expect(store.openTabs()[0]?.preview).toBeFalsy();
  });

  it("activates an already-open tab, restoring its view state when no line is given", () => {
    seed([{ path: "/a.ts", viewState: { scroll: 9 } }], "/a.ts");
    const res = store.openTab("/a.ts");
    expect(res).toEqual({ path: "/a.ts", placement: { viewState: { scroll: 9 } } });
  });

  it("keeps a scratch buffer as a persistent tab, never a preview", () => {
    seed([], null);
    store.openTab("/tmp/Untitled-1", { preview: true, scratch: true });
    expect(store.openTabs()[0]).toMatchObject({ scratch: true });
    expect(store.openTabs()[0]?.preview).toBeFalsy();
  });

  it("keeps a plan tab transient and out of the host's open-editor and persisted session views", () => {
    seed([], null);
    store.openTab("agent-plan:1", { kind: "plan" });

    expect(store.openTabs()[0]).toMatchObject({ path: "agent-plan:1", kind: "plan" });
    expect(openEditorsPushes().at(-1)?.editors).toEqual([]);

    vi.advanceTimersByTime(300);
    const changed = posted.find((m) => m.type === "editor-session-changed") as
      | { session?: { active?: string | null; open?: Array<{ path: string }> } }
      | undefined;
    expect(changed?.session).toEqual({ active: null, open: [] });
  });
});

describe("closeTab", () => {
  it("prefers the right neighbour as the next active tab", () => {
    seed(
      [
        { path: "/a.ts", viewState: null },
        { path: "/b.ts", viewState: null },
        { path: "/c.ts", viewState: null },
      ],
      "/b.ts",
    );
    const res = store.closeTab("/b.ts");
    expect(res?.disposed).toBe("/b.ts");
    expect(res?.next?.path).toBe("/c.ts");
    expect(paths()).toEqual(["/a.ts", "/c.ts"]);
  });

  it("falls back to the left neighbour when closing the last tab", () => {
    seed(
      [
        { path: "/a.ts", viewState: null },
        { path: "/b.ts", viewState: null },
      ],
      "/b.ts",
    );
    expect(store.closeTab("/b.ts")?.next?.path).toBe("/a.ts");
  });

  it("returns null next when the last open tab is closed", () => {
    seed([{ path: "/only.ts", viewState: null }], "/only.ts");
    expect(store.closeTab("/only.ts")).toEqual({ disposed: "/only.ts", next: null });
  });

  it("returns null for a tab that isn't open", () => {
    seed([{ path: "/a.ts", viewState: null }], "/a.ts");
    expect(store.closeTab("/missing.ts")).toBeNull();
  });
});

describe("closeMany", () => {
  it("never closes pinned tabs", () => {
    seed(
      [
        { path: "/pin.ts", viewState: null, pinned: true },
        { path: "/b.ts", viewState: null },
        { path: "/c.ts", viewState: null },
      ],
      "/c.ts",
    );
    const res = store.closeMany(() => true);
    expect(res.disposed.sort()).toEqual(["/b.ts", "/c.ts"]);
    expect(paths()).toEqual(["/pin.ts"]);
    expect(res.next?.path).toBe("/pin.ts");
  });

  it("is a no-op when nothing matches", () => {
    seed([{ path: "/a.ts", viewState: null }], "/a.ts");
    expect(store.closeMany((e) => e.path === "/nope")).toEqual({ disposed: [], next: null });
  });
});

describe("togglePin", () => {
  it("pins a tab, sorting pinned tabs furthest-left and promoting a preview", () => {
    seed(
      [
        { path: "/a.ts", viewState: null },
        { path: "/b.ts", viewState: null, preview: true },
      ],
      "/a.ts",
    );
    store.togglePin("/b.ts");
    expect(paths()).toEqual(["/b.ts", "/a.ts"]);
    expect(store.openTabs()[0]).toMatchObject({ pinned: true });
    expect(store.openTabs()[0]?.preview).toBeFalsy();
    expect(store.activePath()).toBe("/a.ts");
  });

  it("unpins without touching preview", () => {
    seed([{ path: "/a.ts", viewState: null, pinned: true }], "/a.ts");
    store.togglePin("/a.ts");
    expect(store.openTabs()[0]?.pinned).toBeFalsy();
  });
});

describe("convertScratch", () => {
  it("renames the scratch tab in place, keeping its position", () => {
    seed(
      [
        { path: "/x.ts", viewState: null },
        { path: "/tmp/U1", viewState: null, scratch: true },
      ],
      "/tmp/U1",
    );
    const res = store.convertScratch("/tmp/U1", "/proj/real.ts");
    expect(res).toEqual({ path: "/proj/real.ts", placement: { line: 1 } });
    expect(paths()).toEqual(["/x.ts", "/proj/real.ts"]);
    expect(store.openTabs()[1]?.scratch).toBeFalsy();
  });

  it("drops the scratch and activates the existing tab when the save target is already open", () => {
    seed(
      [
        { path: "/proj/real.ts", viewState: { v: 1 } },
        { path: "/tmp/U1", viewState: null, scratch: true },
      ],
      "/tmp/U1",
    );
    const res = store.convertScratch("/tmp/U1", "/proj/real.ts");
    expect(res).toEqual({ path: "/proj/real.ts", placement: { viewState: { v: 1 } } });
    expect(paths()).toEqual(["/proj/real.ts"]);
  });

  it("returns null when the scratch tab isn't open", () => {
    seed([], null);
    expect(store.convertScratch("/tmp/U1", "/proj/real.ts")).toBeNull();
  });
});

describe("dropReviewTab", () => {
  it("removes the review tab and restores the fallback as active", () => {
    seed(
      [
        { path: "weavie-review:1", viewState: null },
        { path: "/a.ts", viewState: null },
      ],
      "weavie-review:1",
    );
    store.dropReviewTab("weavie-review:1", "/a.ts");
    expect(paths()).toEqual(["/a.ts"]);
    expect(store.activePath()).toBe("/a.ts");
  });
});

describe("captureViewState", () => {
  it("records view state without re-pushing the tab set (no structure change)", () => {
    seed([{ path: "/a.ts", viewState: null }], "/a.ts");
    store.captureViewState("/a.ts", { scroll: 3 });
    expect(openEditorsPushes()).toHaveLength(0);
    // The data-only change still reaches the host as a debounced editor-session-changed.
    vi.advanceTimersByTime(300);
    const changed = posted.find((m) => m.type === "editor-session-changed") as
      | { session?: { open?: Array<{ viewState?: unknown }> } }
      | undefined;
    expect(changed?.session?.open?.[0]?.viewState).toEqual({ scroll: 3 });
  });
});

describe("session ownership", () => {
  it("flushEditorSession sends the pending change immediately, stamped with the owner", () => {
    seed([], null, "sess-A");
    store.openTab("/a.ts");
    posted.length = 0;
    store.flushEditorSession();
    const changed = posted.find((m) => m.type === "editor-session-changed");
    expect(changed).toMatchObject({ sessionId: "sess-A" });
    expect(store.editorOwner()).toBe("sess-A");
  });

  it("drops a pending debounced send when the session is switched (no cross-worktree leak)", () => {
    seed([], null, "sess-A");
    store.openTab("/a.ts"); // schedules a debounced send for sess-A
    seed([{ path: "/b.ts", viewState: null }], "/b.ts", "sess-B"); // rebinds + clears the pending timer
    vi.advanceTimersByTime(300);
    // No editor-session-changed for the abandoned sess-A change should have fired.
    expect(posted.some((m) => m.type === "editor-session-changed")).toBe(false);
    expect(store.editorOwner()).toBe("sess-B");
  });

  it("publishes backend and session ownership together", () => {
    seed([], null, "sess-remote", "remote:devbox");
    expect(store.editorBackendId()).toBe("remote:devbox");
    expect(store.editorOwner()).toBe("sess-remote");
  });

  it("keeps a debounced session update on its editor owner during an active-backend handoff", () => {
    seed([], null, "sess-remote", "remote:devbox");
    bridgeState.activeBackendId = "local";

    store.openTab("/remote/a.ts");
    vi.advanceTimersByTime(300);

    expect(posted.find((message) => message.type === "editor-session-changed")).toMatchObject({
      sessionId: "sess-remote",
    });
    expect(postedBackends).toEqual(["remote:devbox", "remote:devbox"]);
  });
});
