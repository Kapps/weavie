import { createComputed, createRoot } from "solid-js";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import { InteractionIntent } from "../chrome/interaction-intent";
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

vi.mock("solid-js", () => import(["solid-js", "dist/solid.js"].join("/")));

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
    signal: new AbortController().signal,
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
      review: null,
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
  it.each([
    false,
    true,
  ])("converts a background scratch without selecting it (destination open: %s)", (existing) => {
    seed(
      [
        { path: "/tmp/U1", scratch: true, viewState: null },
        { path: "/other.ts", viewState: null },
        ...(existing ? [{ path: "/saved.ts", viewState: null }] : []),
      ],
      "/other.ts",
    );
    expect(store.convertScratch("/tmp/U1", "/saved.ts")).toBeNull();
    expect(paths()).toContain("/saved.ts");
    expect(paths()).not.toContain("/tmp/U1");
    expect(store.activePath()).toBe("/other.ts");
  });
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
    store.captureViewState(store.activeTabFor(bridgeState.selected!)!, { scroll: 3 });
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

it("preserves the tab owner through metadata changes and retires it on close or kind replacement", () => {
  seed([], null);
  const session = bridgeState.selected!;
  store.openTab("https://example.test", { kind: "web", preview: true });
  const first = store.activeTabFor(session)!;
  store.captureViewState(first, { reading: 12 });
  store.togglePinFor(session, first.entry.path);
  expect(store.activeTabFor(session)).toBe(first);
  expect(first.signal.aborted).toBe(false);
  store.openTab(first.entry.path, { kind: "source" });
  const replacement = store.activeTabFor(session)!;
  expect(replacement).not.toBe(first);
  expect(first.signal.aborted).toBe(true);
  expect(store.openTabs()).toHaveLength(1);
  expect(replacement.entry.kind).toBe("source");
  store.closeTabFor(session, replacement.entry.path);
  store.openTab(replacement.entry.path, { kind: "source" });
  expect(store.activeTabFor(session)).not.toBe(replacement);
  expect(replacement.signal.aborted).toBe(true);
});

it("closing a captured batch never adopts tabs opened or reopened during its confirmation", async () => {
  const { createTabActions } = await import("./tab-actions");
  seed(
    [
      { path: "/scratch", scratch: true, viewState: null },
      { path: "/old", viewState: null },
    ],
    "/scratch",
  );
  const session = bridgeState.selected!;
  let confirm!: (accepted: boolean) => void;
  const pending = new Promise<boolean>((resolve) => {
    confirm = resolve;
  });
  const present = vi.fn();
  const release = vi.fn();
  const actions = createTabActions({
    captureFocus: new InteractionIntent(new EventTarget()).begin,
    depart: () => {},
    present,
    capture: () => {},
    content: () => "unsaved",
    release,
    confirmDiscard: () => pending,
  });
  const captured = actions.capture(session, undefined);
  const closing = captured.closeAll();
  store.closeTabFor(session, "/old");
  store.openTabFor(session, "/old", {});
  const reopened = store.activeTabFor(session)!;
  store.openTabFor(session, "/new", {});
  confirm(true);
  await closing;
  expect(store.openTabsFor(session).map((entry) => entry.path)).toEqual(["/old", "/new"]);
  expect(reopened.signal.aborted).toBe(false);
  expect(release).toHaveBeenCalledOnce();
  expect(present).not.toHaveBeenCalled();
});

it("a tab menu retains its owner across selection and rejects pinning a reopened resource", () => {
  seed(
    [
      { path: "/a", viewState: null },
      { path: "/b", viewState: null },
    ],
    "/a",
  );
  const session = bridgeState.selected!;
  const first = store.activeTabFor(session)!;
  store.activateTabFor(session, "/b");
  first.assertLive();
  store.closeTabFor(session, "/a");
  store.openTabFor(session, "/a", {});
  expect(() => first.assertLive()).toThrow("closed");
});

it("captures every exact position without invalidating reactive tab metadata or past snapshots", () => {
  seed([{ path: "/scroll", viewState: { top: 0 } }], "/scroll");
  const session = bridgeState.selected!;
  const tab = store.activeTabFor(session)!;
  const before = store.snapshotEditorSessionFor(session)!;
  const topology = store.openTabsFor(session);
  const structure = vi.fn();
  const off = store.onEditorSessionChanged(session, structure);
  createRoot((dispose) => {
    const read = vi.fn();
    createComputed(() => read(store.openTabsFor(session), store.activePathFor(session)));
    for (let top = 0; top < 3000; top++) store.captureViewState(tab, { top: top + 0.25 });
    expect(tab.viewState).toEqual({ top: 2999.25 });
    expect(store.openTabsFor(session)).toBe(topology);
    expect(topology[0]).not.toHaveProperty("viewState");
    expect(read).toHaveBeenCalledOnce();
    expect(structure).toHaveBeenCalledOnce();
    const after = store.snapshotEditorSessionFor(session)!;
    expect(after.open[0]!.viewState).toEqual({ top: 2999.25 });
    expect(before.open[0]!.viewState).toEqual({ top: 0 });
    expect(after.open).not.toBe(before.open);
    expect(after.open[0]).not.toBe(before.open[0]);
    expect(store.activateTabFor(session, tab.entry.path)?.placement).toEqual({
      viewState: { top: 2999.25 },
    });
    dispose();
  });
  off();
});

it("persists the latest saved position across intervening metadata commits and explicit flushes", () => {
  seed([{ path: "/scroll", viewState: null }], "/scroll");
  const session = bridgeState.selected!;
  const tab = store.activeTabFor(session)!;
  store.captureViewState(tab, { top: 12.5 });
  store.togglePinFor(session, tab.entry.path);
  store.openTabFor(session, "/other", {});
  store.captureViewState(tab, { top: 19.25 });
  vi.advanceTimersByTime(300);
  const changed = bridgeState.posted.filter((message) => message.name === "sessionChanged");
  expect(changed).toHaveLength(1);
  expect(changed[0]!.payload.session).toMatchObject({
    active: "/other",
    open: [{ path: "/scroll", pinned: true, viewState: { top: 19.25 } }, { path: "/other" }],
  });
  store.closeTabFor(session, "/other");
  store.captureViewState(tab, { top: 33.75 });
  store.flushEditorSessionFor(session);
  expect(bridgeState.posted.at(-1)?.payload.session).toMatchObject({
    active: "/scroll",
    open: [{ path: "/scroll", viewState: { top: 33.75 } }],
  });
  expect(changed[0]!.payload.session).toMatchObject({ open: [{ viewState: { top: 19.25 } }, {}] });
  expect(vi.getTimerCount()).toBe(0);
});

it("authoritative restore replaces saved positions and cancels an obsolete pending send", () => {
  seed([{ path: "/scroll", viewState: null }], "/scroll");
  const session = bridgeState.selected!;
  const tab = store.activeTabFor(session)!;
  store.captureViewState(tab, { top: 999 });
  seed([{ path: "/scroll", viewState: { top: 3.5 } }], "/scroll");
  expect(store.activeTabFor(session)).toBe(tab);
  expect(tab.viewState).toEqual({ top: 3.5 });
  vi.advanceTimersByTime(300);
  expect(bridgeState.posted.filter((message) => message.name === "sessionChanged")).toEqual([]);
});

it("rejects a retired tab capture instead of overwriting its same-path replacement", () => {
  seed([{ path: "/scroll", viewState: null }], "/scroll");
  const session = bridgeState.selected!;
  const old = store.activeTabFor(session)!;
  store.closeTabFor(session, old.entry.path);
  store.openTabFor(session, old.entry.path, {});
  const current = store.activeTabFor(session)!;
  store.captureViewState(current, { top: 4 });
  store.captureViewState(old, { top: 900 });
  expect(current.viewState).toEqual({ top: 4 });
});

it("captures an unselected owner without touching the same path in the selected session", () => {
  seed([{ path: "/same", viewState: null }], "/same", "reading-a");
  const a = store.activeTabFor(bridgeState.selected!)!;
  seed([{ path: "/same", viewState: { top: 8 } }], "/same", "reading-b");
  const b = store.activeTabFor(bridgeState.selected!)!;
  store.captureViewState(a, { top: 16.5 });
  store.flushEditorSessionFor(a.session);
  expect(a.viewState).toEqual({ top: 16.5 });
  expect(b.viewState).toEqual({ top: 8 });
  expect(bridgeState.posted.at(-1)).toMatchObject({ slot: "reading-a" });
});

it("closed-tab undo restores the final synchronous capture into a new exact owner", async () => {
  const { createTabActions } = await import("./tab-actions");
  seed([{ path: "/scroll", viewState: { top: 1 } }], "/scroll");
  const session = bridgeState.selected!;
  const old = store.activeTabFor(session)!;
  const present = vi.fn();
  const actions = createTabActions({
    captureFocus: new InteractionIntent(new EventTarget()).begin,
    depart: () => {},
    present,
    capture: (tab) => store.captureViewState(tab, { top: 7.5, selection: [3, 6] }),
    content: () => "",
    release: () => {},
    confirmDiscard: async () => true,
  });
  await actions.capture(session, "/scroll").close();
  expect(old.signal.aborted).toBe(true);
  expect(actions.capture(session, undefined).reopenClosed()).toBe(true);
  const current = store.activeTabFor(session)!;
  expect(current).not.toBe(old);
  expect(current.viewState).toEqual({ top: 7.5, selection: [3, 6] });
  expect(present).toHaveBeenLastCalledWith(
    session,
    {
      path: "/scroll",
      placement: { viewState: { top: 7.5, selection: [3, 6] } },
    },
    expect.objectContaining({ current: expect.any(Function) }),
  );
});
