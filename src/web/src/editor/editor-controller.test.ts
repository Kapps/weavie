import { beforeEach, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import type { EditorControllerDeps } from "./editor-controller";

const env = vi.hoisted(() => ({
  selected: null as ClientSession | null,
  installers: [] as Array<(session: ClientSession) => undefined | (() => void)>,
  selections: new Set<(session: ClientSession | null) => void>(),
}));

vi.mock("../bridge", () => ({
  LOCAL_BACKEND_ID: "local",
  hostConnection: () => undefined,
  clientSessionAt: () => null,
  isBrowserHostedShell: () => false,
  log: () => {},
  onSelectedSession: (listener: (session: ClientSession | null) => void) => {
    env.selections.add(listener);
    listener(env.selected);
    return () => env.selections.delete(listener);
  },
  registerSessionFeature: (installer: (session: ClientSession) => undefined | (() => void)) => {
    env.installers.push(installer);
    return () => {};
  },
  selectedSession: () => env.selected,
}));

vi.stubGlobal("location", { search: "" });
vi.stubGlobal("window", {});
const { activePathFor, openTabsFor, tabOwnerFor, openTabFor, snapshotEditorSessionFor } =
  await import("./session-store");
const { createEditorController } = await import("./editor-controller");
const coreInstallers = env.installers.length;

beforeEach(() => {
  env.selections.clear();
  env.installers.length = coreInstallers;
});

interface FakeFeature {
  handlers: Map<string, Array<(message: unknown) => void>>;
  requests: Map<string, (message: unknown) => unknown>;
  published: Array<{ name: string; payload: unknown }>;
  emit(name: string, message: unknown): void;
  handle(name: string, handler: (message: unknown) => unknown): () => void;
  on(name: string, handler: (message: unknown) => void): () => void;
  publish(name: string, payload: unknown): void;
}

function fakeSession(slot: string): ClientSession {
  const features = new Map<string, FakeFeature>();
  const feature = (name: string): FakeFeature => {
    let current = features.get(name);
    if (current !== undefined) {
      return current;
    }
    const handlers = new Map<string, Array<(message: unknown) => void>>();
    const requests = new Map<string, (message: unknown) => unknown>();
    current = {
      handlers,
      requests,
      published: [],
      emit(event, message) {
        for (const handler of handlers.get(event) ?? []) {
          handler(message);
        }
      },
      handle(event, handler) {
        requests.set(event, handler);
        return () => {
          requests.delete(event);
        };
      },
      on(event, handler) {
        const listeners = handlers.get(event) ?? [];
        listeners.push(handler);
        handlers.set(event, listeners);
        return () => {};
      },
      publish(event, payload) {
        current?.published.push({ name: event, payload });
      },
    };
    features.set(name, current);
    return current;
  };
  return {
    signal: new AbortController().signal,
    address: { slot, incarnation: "1" },
    connection: { id: "local", isLocal: true, reportError: () => {} },
    feature,
    state: {
      editor: { current: null, subscribe: () => () => {} },
    },
  } as unknown as ClientSession;
}

function dependencies(confirm: EditorControllerDeps["confirm"]): EditorControllerDeps {
  return {
    confirm,
    confirmDiscard: () => Promise.resolve(true),
    onEditorContextMenu: () => {},
    onCurrentFileChanged: () => {},
    onDestinationActivated: () => {},
    onOpenError: () => {},
    onSaveError: () => {},
    promptRevision: () => Promise.resolve(null),
    promptScratchName: () => Promise.resolve(null),
  };
}

it("the latest reveal retains its placement while the editor has not initialized", async () => {
  const session = fakeSession("pending-reveal");
  env.selected = session;
  const onOpenError = vi.fn();
  const controller = createEditorController({
    ...dependencies(() => Promise.resolve(true)),
    onOpenError,
  });
  for (const install of env.installers) install(session);

  controller.openFile("/work/sample.txt", 7);
  controller.openFile("/work/sample.txt", 3);
  const restore = vi.fn(async () => {});
  const tab = tabOwnerFor(session, "/work/sample.txt");
  if (tab === undefined) throw new Error("The reveal must open a tab.");
  tab.mount({
    text: true,
    capture: () => ({ state: null, text: null }),
    restore,
    focus: () => {},
    actions: () => undefined,
  });

  await vi.waitFor(() =>
    expect(restore).toHaveBeenCalledExactlyOnceWith({ line: 3 }, expect.any(AbortSignal)),
  );
  expect(onOpenError).not.toHaveBeenCalled();
});

it("reverts an unfocused review board through its exact session", async () => {
  const selected = fakeSession("selected");
  const owner = fakeSession("owner");
  env.selected = selected;
  const confirm = vi.fn(() => Promise.resolve(true));
  const controller = createEditorController(dependencies(confirm));
  for (const install of env.installers) {
    install(owner);
  }
  const review = owner.feature("review") as unknown as FakeFeature;
  const file = {
    path: "/owner/change.ts",
    name: "change.ts",
    added: 1,
    removed: 1,
    line: 1,
    currentExists: true,
  };
  const unloaded = {
    path: "/owner/lazy.ts",
    name: "lazy.ts",
    added: 1,
    removed: 0,
    line: 2,
    currentExists: true,
  };
  review.emit("changes", { label: "owner", files: [file, unloaded] });
  review.emit("diff", {
    path: file.path,
    name: file.name,
    acceptedBaseline: "before",
    acceptedBaselineExists: true,
    baseline: "before",
    baselineExists: true,
    current: "after",
    currentExists: true,
  });

  expect(controller.review.revert(owner)).toBe(true);
  await vi.waitFor(() =>
    expect(review.published).toContainEqual({ name: "revertAll", payload: {} }),
  );
  expect(confirm).toHaveBeenCalledWith(expect.objectContaining({ title: "Revert all changes?" }));
});

it("keeps a deleted ref entry until the authoritative review change list removes it", () => {
  const session = fakeSession("deleted-review");
  env.selected = session;
  const controller = createEditorController(dependencies(() => Promise.resolve(true)));
  for (const install of env.installers) {
    install(session);
  }
  const review = session.feature("review") as unknown as FakeFeature;
  const files = session.feature("files") as unknown as FakeFeature;
  const deleted = {
    path: "/owner/deleted.ts",
    name: "deleted.ts",
    added: 0,
    removed: 2,
    line: 1,
    currentExists: false,
  };
  review.emit("changes", { label: "vs HEAD", files: [deleted] });

  files.emit("changed", { changes: [{ path: deleted.path, kind: "deleted" }] });

  expect(controller.review.overview().files.map((file) => file.summary())).toEqual([deleted]);

  review.emit("changes", { label: "vs HEAD", files: [] });
  expect(controller.review.overview().files).toHaveLength(0);
});

it("opens unified review explicitly as a tab while files and proposals use normal file tabs", () => {
  const session = fakeSession("held-review");
  env.selected = session;
  const controller = createEditorController(dependencies(() => Promise.resolve(true)));
  for (const install of env.installers) {
    install(session);
  }
  const review = session.feature("review") as unknown as FakeFeature;
  const editor = session.feature("editor") as unknown as FakeFeature;
  const file = {
    path: "/work/held.ts",
    name: "held.ts",
    added: 2,
    removed: 0,
    line: 3,
    currentExists: true,
  };
  review.emit("changes", { label: "turn", files: [file] });
  expect(openTabsFor(session)).toHaveLength(0);
  expect(controller.openReview(session, undefined, undefined)).toBe(true);
  expect(activePathFor(session)).toBe("weavie:review");

  // File reveals activate their own tab and retain Review in the strip.
  editor.emit("openFile", { path: "/work/other.ts", line: null, intent: "reveal" });
  expect(activePathFor(session)).toBe("/work/other.ts");
  expect(openTabsFor(session).some((tab) => tab.kind === "review")).toBe(true);

  // Rejecting a temporary proposal tab returns to the prior normal tab.
  editor.emit("showDiff", {
    id: "diff-1",
    path: "/work/held.ts",
    tabName: "held.ts",
    original: "before",
    proposed: "after",
  });
  expect(activePathFor(session)).toBe("/work/held.ts");
  editor.emit("closeDiff", { id: "diff-1" });
  expect(activePathFor(session)).toBe("/work/other.ts");
  expect(openTabsFor(session).some((tab) => tab.kind === "review")).toBe(true);

  // Explicit navigation follows the same selection path.
  editor.emit("openFile", { path: "/work/other.ts", line: null, intent: "navigation" });
  expect(activePathFor(session)).toBe("/work/other.ts");
});

it("captures and flushes the departing exact tab after selection has moved to another session", () => {
  const first = fakeSession("reading-first");
  const second = fakeSession("reading-second");
  env.selected = first;
  createEditorController(dependencies(async () => true));
  for (const install of env.installers) {
    install(first);
    install(second);
  }
  openTabFor(first, "weavie:review", { kind: "review" });
  openTabFor(second, "weavie:review", { kind: "review" });
  tabOwnerFor(first, "weavie:review")!.mount({
    text: false,
    capture: () => ({ state: { top: 19.75, section: "first.ts" }, text: null }),
    restore: async () => {},
    focus: () => {},
    actions: () => undefined,
  });
  const secondCapture = vi.fn(() => ({ state: { top: 999 }, text: null }));
  tabOwnerFor(second, "weavie:review")!.mount({
    text: false,
    capture: secondCapture,
    restore: async () => {},
    focus: () => {},
    actions: () => undefined,
  });
  env.selected = second;
  for (const selection of env.selections) selection(second);
  expect((first.feature("editor") as unknown as FakeFeature).published.at(-1)).toMatchObject({
    name: "sessionChanged",
    payload: { session: { open: [{ viewState: { top: 19.75, section: "first.ts" } }] } },
  });
  expect(snapshotEditorSessionFor(second)?.open[0]?.viewState).toBeNull();
  expect(secondCapture).not.toHaveBeenCalled();
});

it("flush returns the current exact presenter snapshot after the asynchronous flush boundary", async () => {
  const session = fakeSession("flush-reading");
  env.selected = session;
  createEditorController(dependencies(async () => true));
  for (const install of env.installers) install(session);
  openTabFor(session, "weavie:review", { kind: "review" });
  let top = 1;
  tabOwnerFor(session, "weavie:review")!.mount({
    text: false,
    capture: () => ({ state: { top }, text: null }),
    restore: async () => {},
    focus: () => {},
    actions: () => undefined,
  });
  const editor = session.feature("editor") as unknown as FakeFeature;
  const flushed = editor.requests.get("flush")!({});
  top = 24.5;
  await expect(flushed).resolves.toMatchObject({
    session: { open: [{ path: "weavie:review", viewState: { top: 24.5 } }] },
  });
  expect(editor.published.at(-1)).toMatchObject({
    name: "sessionChanged",
    payload: { session: { open: [{ viewState: { top: 24.5 } }] } },
  });
});
