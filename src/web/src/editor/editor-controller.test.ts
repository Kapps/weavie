import { expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import type { EditorControllerDeps } from "./editor-controller";
import type { EditorHost, ReviewCopyScope } from "./editor-host";

const env = vi.hoisted(() => ({
  selected: null as ClientSession | null,
  installers: [] as Array<(session: ClientSession) => undefined | (() => void)>,
}));

vi.mock("../bridge", () => ({
  clientSessionAt: () => null,
  isBrowserHostedShell: () => false,
  log: () => {},
  onSelectedSession: (listener: (session: ClientSession | null) => void) => {
    listener(env.selected);
    return () => {};
  },
  registerSessionFeature: (installer: (session: ClientSession) => undefined | (() => void)) => {
    env.installers.push(installer);
    return () => {};
  },
  selectedSession: () => env.selected,
}));

vi.stubGlobal("location", { search: "" });
vi.stubGlobal("window", {});
const { createDeferredReviewCopyScope, createEditorController } = await import(
  "./editor-controller"
);

interface FakeFeature {
  handlers: Map<string, Array<(message: unknown) => void>>;
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
    current = {
      handlers,
      published: [],
      emit(event, message) {
        for (const handler of handlers.get(event) ?? []) {
          handler(message);
        }
      },
      handle: () => () => {},
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
    focusVisibleOverlay: () => false,
    onCurrentFileChanged: () => {},
    onDestinationActivated: () => {},
    onOpenError: () => {},
    onSaveError: () => {},
    promptRevision: () => Promise.resolve(null),
    promptScratchName: () => Promise.resolve(null),
  };
}

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

it("upgrades an early unified-review scope when the editor host becomes ready", async () => {
  let resolveHost!: (host: Pick<EditorHost, "createReviewCopyScope">) => void;
  const hostReady = new Promise<Pick<EditorHost, "createReviewCopyScope">>((resolve) => {
    resolveHost = resolve;
  });
  const realScope: ReviewCopyScope = {
    open: async () => ({ model: {} as never, editable: true }),
    dispose: vi.fn(),
  };
  const open = vi.spyOn(realScope, "open");
  const deferred = createDeferredReviewCopyScope(hostReady);
  const session = fakeSession("early-review");

  const opening = deferred.open(session, "/work/review.ts", "current", true);
  expect(open).not.toHaveBeenCalled();
  resolveHost({ createReviewCopyScope: () => realScope });

  await expect(opening).resolves.toMatchObject({ editable: true });
  expect(open).toHaveBeenCalledWith(session, "/work/review.ts", "current", true);
  deferred.dispose();
  expect(realScope.dispose).toHaveBeenCalledOnce();
});

it("holds unified review through the agent's reveals and hands it back after a proposal", () => {
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
  expect(controller.review.toggleMode(session)).toBe(true);
  expect(controller.review.mode()).toBe("unified");

  // The agent revealing a file lands behind the overview.
  editor.emit("openFile", { path: "/work/other.ts", line: null, intent: "reveal" });
  expect(controller.review.mode()).toBe("unified");

  // A proposal is a gate, so it takes the pane — and gives it back when it closes.
  editor.emit("showDiff", {
    id: "diff-1",
    path: "/work/held.ts",
    tabName: "held.ts",
    original: "before",
    proposed: "after",
  });
  expect(controller.review.mode()).toBe("file");
  editor.emit("closeDiff", { id: "diff-1" });
  expect(controller.review.mode()).toBe("unified");

  // The user navigating somewhere does leave it.
  editor.emit("openFile", { path: "/work/other.ts", line: null, intent: "navigation" });
  expect(controller.review.mode()).toBe("file");
});
