import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import type { EditorSessionEntry } from "./session-types";

interface Posted {
  backendId: string;
  slot: string;
  feature: string;
  name: string;
  payload: Record<string, unknown>;
}

interface FakeSession {
  client: ClientSession;
  restore?: (session: { active: string | null; open: EditorSessionEntry[] }) => void;
}

const bridgeState = vi.hoisted(() => ({
  installer: undefined as ((session: ClientSession) => undefined | (() => void)) | undefined,
  selected: null as ClientSession | null,
  sessions: new Map<string, FakeSession>(),
  posted: [] as Posted[],
}));

vi.mock("../bridge", () => ({
  registerSessionFeature: (installer: (session: ClientSession) => undefined | (() => void)) => {
    bridgeState.installer = installer;
    return () => {};
  },
  selectedSession: () => bridgeState.selected,
}));

const store = await import("./session-store");

type Entry = EditorSessionEntry;

function fakeSession(backendId: string, owner: string): FakeSession {
  const key = `${backendId}\u0000${owner}`;
  const existing = bridgeState.sessions.get(key);
  if (existing !== undefined) {
    return existing;
  }
  const fake = {} as FakeSession;
  const client = {
    connection: {
      id: backendId,
      reportError: (error: unknown) => {
        throw error;
      },
    },
    address: {
      slot: owner,
      incarnation: owner,
    },
    state: {
      editor: {
        subscribe: (
          listener: (session: { active: string | null; open: EditorSessionEntry[] }) => void,
        ) => {
          fake.restore = listener;
          return () => {};
        },
      },
    },
    feature: (feature: string) => ({
      publish: (name: string, payload: Record<string, unknown>) => {
        bridgeState.posted.push({ backendId, slot: owner, feature, name, payload });
      },
    }),
  } as unknown as ClientSession;
  fake.client = client;
  bridgeState.sessions.set(key, fake);
  bridgeState.installer?.(client);
  return fake;
}

// Restore one session-owned editor store and select it for the convenience exports.
function seed(open: Entry[], active: string | null, owner = "sess-1", backendId = "local"): void {
  const fake = fakeSession(backendId, owner);
  bridgeState.selected = fake.client;
  fake.restore?.({ active, open });
  bridgeState.posted.length = 0;
}

const openEditorsPushes = (): Posted[] =>
  bridgeState.posted.filter(
    (message) => message.feature === "editor" && message.name === "openEditorsChanged",
  );
const paths = (): string[] => store.openTabs().map((entry) => entry.path);

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

  it("reveals an explicit line 1 in an already-open tab (a created file's whole diff)", () => {
    seed([{ path: "/new.ts", viewState: { scroll: 9 } }], "/new.ts");
    const res = store.openTab("/new.ts", { line: 1 });
    expect(res).toEqual({ path: "/new.ts", placement: { line: 1 } });
  });

  it("keeps a scratch buffer as a persistent tab, never a preview", () => {
    seed([], null);
    store.openTab("/tmp/Untitled-1", { preview: true, scratch: true });
    expect(store.openTabs()[0]).toMatchObject({ scratch: true });
    expect(store.openTabs()[0]?.preview).toBeFalsy();
  });

  it("keeps a plan out of agent file context while preserving it in the session snapshot", () => {
    seed([], null);
    store.openTab("agent-plan:1", { kind: "plan" });

    expect(store.openTabs()[0]).toMatchObject({ path: "agent-plan:1", kind: "plan" });
    expect(openEditorsPushes().at(-1)?.payload.editors).toEqual([]);

    vi.advanceTimersByTime(300);
    const changed = bridgeState.posted.find((message) => message.name === "sessionChanged");
    expect(changed?.payload.session).toEqual({
      active: "agent-plan:1",
      open: [{ path: "agent-plan:1", kind: "plan", viewState: null }],
    });
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
    const changed = bridgeState.posted.find((message) => message.name === "sessionChanged");
    const session = changed?.payload.session as
      | { open?: Array<{ viewState?: unknown }> }
      | undefined;
    expect(session?.open?.[0]?.viewState).toEqual({ scroll: 3 });
  });
});

describe("session ownership", () => {
  it("flushEditorSession sends the pending change on the owning session bus", () => {
    seed([], null, "sess-A");
    store.openTab("/a.ts");
    bridgeState.posted.length = 0;
    store.flushEditorSession();
    const changed = bridgeState.posted.find((message) => message.name === "sessionChanged");
    expect(changed).toMatchObject({ backendId: "local", slot: "sess-A" });
    expect(store.editorOwner()).toBe("sess-A");
  });

  it("flushEditorSessionFor drains the exact owner even while another session is selected", () => {
    seed([], null, "sess-A");
    const first = bridgeState.selected!;
    store.openTab("/a.ts");
    seed([{ path: "/b.ts", viewState: null }], "/b.ts", "sess-B");
    bridgeState.posted.length = 0;

    store.flushEditorSessionFor(first);

    expect(bridgeState.posted.find((message) => message.name === "sessionChanged")).toMatchObject({
      backendId: "local",
      slot: "sess-A",
    });
  });

  it("keeps a pending debounced send on its owner when another session is selected", () => {
    seed([], null, "sess-A");
    store.openTab("/a.ts"); // schedules a debounced send for sess-A
    seed([{ path: "/b.ts", viewState: null }], "/b.ts", "sess-B");
    vi.advanceTimersByTime(300);
    expect(bridgeState.posted.find((message) => message.name === "sessionChanged")).toMatchObject({
      backendId: "local",
      slot: "sess-A",
    });
    expect(store.editorOwner()).toBe("sess-B");
  });

  it("publishes backend and session ownership together", () => {
    seed([], null, "sess-remote", "remote:devbox");
    expect(store.editorBackendId()).toBe("remote:devbox");
    expect(store.editorOwner()).toBe("sess-remote");
  });

  it("keeps a debounced session update on its editor owner during cross-host selection", () => {
    seed([], null, "sess-remote", "remote:devbox");

    store.openTab("/remote/a.ts");
    vi.advanceTimersByTime(300);

    expect(bridgeState.posted.find((message) => message.name === "sessionChanged")).toMatchObject({
      backendId: "remote:devbox",
      slot: "sess-remote",
    });
  });
});
