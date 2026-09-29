import { beforeEach, expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import { InteractionIntent } from "../chrome/interaction-intent";
import { omnibarRequest } from "../chrome/omnibar-controller";
import { type FileReferenceResolution, openFilesIn, revealFileIn } from "../files/reveal";
import { PAGE_EPOCH } from "../messaging/page-epoch";
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
vi.stubGlobal("window", { clearTimeout });
const {
  activePathFor,
  openTabsFor,
  tabOwnerFor,
  openTabFor,
  closeTabFor,
  snapshotEditorSessionFor,
} = await import("./session-store");
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
  requested: Array<{ name: string; payload: unknown }>;
  emit(name: string, message: unknown): void;
  handle(name: string, handler: (message: unknown) => unknown): () => void;
  on(name: string, handler: (message: unknown) => void): () => void;
  publish(name: string, payload: unknown): void;
  request(name: string, payload: unknown): Promise<unknown>;
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
    if (name === "editor") requests.set("commitFileOpens", () => true);
    current = {
      handlers,
      requests,
      published: [],
      requested: [],
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
      request: async (event, payload) => {
        current?.requested.push({ name: event, payload });
        const handler = requests.get(event);
        if (handler === undefined) throw new Error(`No test responder for ${event}`);
        return handler(payload);
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
    interaction: new InteractionIntent(new EventTarget()),
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
    captureReviewAdvance: () => () => {},
  });

  await vi.waitFor(() =>
    expect(restore).toHaveBeenCalledExactlyOnceWith({ line: 3 }, expect.any(AbortSignal)),
  );
  expect(onOpenError).not.toHaveBeenCalled();
});

it.each([
  "before mount",
  "during restore",
])("finishes the selected placement without reclaiming focus after newer input %s", async (when) => {
  const session = fakeSession(`slow-open-${when}`);
  env.selected = session;
  const deps = dependencies(async () => true);
  const controller = createEditorController(deps);
  for (const install of env.installers) install(session);
  controller.openFile("/work/slow.txt", 7);
  const tab = tabOwnerFor(session, "/work/slow.txt")!;
  const completion = Promise.withResolvers<void>();
  const restore = vi.fn(() => completion.promise);
  const focus = vi.fn();
  if (when === "before mount") deps.interaction.invalidate();
  tab.mount({
    text: true,
    capture: () => ({ state: null, text: null }),
    restore,
    focus,
    actions: () => undefined,
    captureReviewAdvance: () => () => {},
  });
  await vi.waitFor(() => expect(restore).toHaveBeenCalledOnce());
  if (when === "during restore") deps.interaction.invalidate();
  completion.resolve();
  await completion.promise;
  await Promise.resolve();
  expect(restore).toHaveBeenCalledExactlyOnceWith({ line: 7 }, expect.any(AbortSignal));
  expect(activePathFor(session)).toBe("/work/slow.txt");
  expect(focus).not.toHaveBeenCalled();
  controller.dispose();
});

it("background session open and close notifications cannot revoke foreground focus", async () => {
  const foreground = fakeSession("foreground-loading");
  const background = fakeSession("background-opening");
  env.selected = foreground;
  const deps = dependencies(async () => true);
  const controller = createEditorController(deps);
  for (const install of env.installers) {
    install(foreground);
    install(background);
  }
  controller.openFile("/foreground.txt", 3);
  const permission = deps.interaction.capture();
  const editor = background.feature("editor") as unknown as FakeFeature;
  editor.emit("openFile", { path: "/background.txt", line: 1, intent: "navigation" });
  expect(activePathFor(background)).toBe("/background.txt");
  expect(permission.current()).toBe(true);
  editor.emit("closeTab", { path: "/background.txt" });
  expect(permission.current()).toBe(true);
  const focus = vi.fn();
  tabOwnerFor(foreground, "/foreground.txt")!.mount({
    text: true,
    capture: () => ({ state: null, text: null }),
    restore: async () => {},
    focus,
    actions: () => undefined,
    captureReviewAdvance: () => () => {},
  });
  await vi.waitFor(() => expect(focus).toHaveBeenCalledOnce());
  controller.dispose();
});

it.each<FileReferenceResolution>([
  { kind: "file", path: "/work/resolved.ts", line: 9 },
  { kind: "ambiguous", query: "resolved.ts", line: 9 },
])("a stale file-reference result cannot initiate presentation: $kind", async (result) => {
  const session = fakeSession(`resolution-${result.kind}`);
  env.selected = session;
  const deps = dependencies(async () => true);
  const controller = createEditorController(deps);
  for (const install of env.installers) install(session);
  const response = Promise.withResolvers<FileReferenceResolution>();
  (session.feature("files") as unknown as FakeFeature).requests.set(
    "resolveReference",
    () => response.promise,
  );
  const priorPalette = omnibarRequest();
  const completion = revealFileIn(session, "resolved.ts", 9, true);
  deps.interaction.invalidate();
  response.resolve(result);
  await completion;
  expect(openTabsFor(session)).toHaveLength(0);
  expect(omnibarRequest()).toBe(priorPalette);
  expect((session.feature("editor") as unknown as FakeFeature).published).toHaveLength(0);
  controller.dispose();
});

it("only the newest file resolver commits and immediately persists the owned tab", async () => {
  const session = fakeSession("resolutions-out-of-order");
  env.selected = session;
  const controller = createEditorController(dependencies(async () => true));
  for (const install of env.installers) install(session);
  const older = Promise.withResolvers<FileReferenceResolution>();
  const newer = Promise.withResolvers<FileReferenceResolution>();
  (session.feature("files") as unknown as FakeFeature).requests.set(
    "resolveReference",
    (request) =>
      (request as { path: string }).path === "older.ts" ? older.promise : newer.promise,
  );
  const first = revealFileIn(session, "older.ts", undefined, false);
  const second = revealFileIn(session, "newer.ts", 7, true);
  newer.resolve({ kind: "file", path: "/work/newer.ts", line: 7 });
  await second;
  older.resolve({ kind: "file", path: "/work/older.ts", line: null });
  await first;
  expect(activePathFor(session)).toBe("/work/newer.ts");
  expect(openTabsFor(session).map((tab) => tab.path)).toEqual(["/work/newer.ts"]);
  expect((session.feature("editor") as unknown as FakeFeature).requested.at(-1)).toMatchObject({
    name: "commitFileOpens",
    payload: {
      activePath: "/work/newer.ts",
      files: [{ path: "/work/newer.ts", preview: true }],
    },
  });
  controller.dispose();
});

it("cancelling an unresolved destination cannot strand the selected file's ongoing load", async () => {
  const session = fakeSession("loading-before-resolution");
  env.selected = session;
  const deps = dependencies(async () => true);
  const controller = createEditorController(deps);
  for (const install of env.installers) install(session);
  controller.openFile("/work/loading.ts", 7);
  const load = Promise.withResolvers<void>();
  const restore = vi.fn((_placement: unknown, _signal: AbortSignal) => load.promise);
  const focus = vi.fn();
  tabOwnerFor(session, "/work/loading.ts")!.mount({
    text: true,
    capture: () => ({ state: null, text: null }),
    restore,
    focus,
    actions: () => undefined,
    captureReviewAdvance: () => () => {},
  });
  await vi.waitFor(() => expect(restore).toHaveBeenCalledOnce());
  const response = Promise.withResolvers<FileReferenceResolution>();
  (session.feature("files") as unknown as FakeFeature).requests.set(
    "resolveReference",
    () => response.promise,
  );
  const completion = revealFileIn(session, "later.ts", undefined, false);
  deps.interaction.invalidate();
  response.resolve({ kind: "file", path: "/work/later.ts", line: null });
  await completion;
  expect(restore.mock.calls[0]![1].aborted).toBe(false);
  expect(activePathFor(session)).toBe("/work/loading.ts");
  expect(openTabsFor(session)).toHaveLength(1);
  load.resolve();
  await load.promise;
  expect(focus).not.toHaveBeenCalled();
  controller.dispose();
});

it("background file resolution persists to its owner without revoking foreground intent", async () => {
  const foreground = fakeSession("foreground-resolver");
  const background = fakeSession("background-resolver");
  env.selected = foreground;
  const deps = dependencies(async () => true);
  const controller = createEditorController(deps);
  for (const install of env.installers) {
    install(foreground);
    install(background);
  }
  (background.feature("files") as unknown as FakeFeature).requests.set("resolveReference", () => ({
    kind: "file",
    path: "/background.ts",
    line: null,
  }));
  const focus = deps.interaction.begin();
  await revealFileIn(background, "/background.ts", undefined, false);
  expect(activePathFor(background)).toBe("/background.ts");
  expect(activePathFor(foreground)).toBeNull();
  expect(focus.current()).toBe(true);
  expect((background.feature("editor") as unknown as FakeFeature).requested).toContainEqual({
    name: "commitFileOpens",
    payload: {
      files: [{ path: "/background.ts", preview: false }],
      activePath: "/background.ts",
      originPageEpoch: PAGE_EPOCH,
    },
  });
  controller.dispose();
});

it("late OS batches preserve every file without replacing newer navigation or focus", async () => {
  const session = fakeSession("durable-batches");
  env.selected = session;
  const deps = dependencies(async () => true);
  const controller = createEditorController(deps);
  for (const install of env.installers) install(session);
  const held = Promise.withResolvers<FileReferenceResolution>();
  (session.feature("files") as unknown as FakeFeature).requests.set(
    "resolveReference",
    (message) => {
      const { path } = message as { path: string };
      return path === "one.ts" ? held.promise : { kind: "file", path: `/work/${path}`, line: null };
    },
  );
  const first = openFilesIn(session, ["one.ts", "two.ts"]);
  await openFilesIn(session, ["three.ts"]);
  controller.openFile("/work/reading.ts", undefined);
  const focus = deps.interaction.begin();
  held.resolve({ kind: "file", path: "/work/one.ts", line: null });
  await first;
  expect(openTabsFor(session).map((tab) => tab.path)).toEqual([
    "/work/three.ts",
    "/work/reading.ts",
    "/work/one.ts",
    "/work/two.ts",
  ]);
  expect(activePathFor(session)).toBe("/work/reading.ts");
  expect(focus.current()).toBe(true);
  expect((session.feature("editor") as unknown as FakeFeature).requested.at(-1)).toEqual({
    name: "commitFileOpens",
    payload: {
      files: [
        { path: "/work/one.ts", preview: false },
        { path: "/work/two.ts", preview: false },
      ],
      activePath: null,
      originPageEpoch: PAGE_EPOCH,
    },
  });
  controller.dispose();
});

it("file-open persistence failures stay visible after the accepted navigation departs", async () => {
  const session = fakeSession("commit-failure");
  env.selected = session;
  const deps = dependencies(async () => true);
  deps.onOpenError = vi.fn();
  const controller = createEditorController(deps);
  for (const install of env.installers) install(session);
  (session.feature("files") as unknown as FakeFeature).requests.set("resolveReference", () => ({
    kind: "file",
    path: "/work/file.ts",
    line: null,
  }));
  (session.feature("editor") as unknown as FakeFeature).requests.set("commitFileOpens", () => {
    throw new Error("Persistence unavailable");
  });
  await revealFileIn(session, "file.ts", undefined, false);
  expect(deps.onOpenError).toHaveBeenCalledWith(expect.stringContaining("Persistence unavailable"));
  controller.dispose();
});

it("a batch keeps its valid files and shows ambiguity without activating over the picker", async () => {
  const session = fakeSession("mixed-batch");
  env.selected = session;
  const deps = dependencies(async () => true);
  deps.onOpenError = vi.fn();
  const controller = createEditorController(deps);
  for (const install of env.installers) install(session);
  controller.openFile("/work/reading.ts", undefined);
  (session.feature("files") as unknown as FakeFeature).requests.set(
    "resolveReference",
    (message) => {
      const { path } = message as { path: string };
      if (path === "missing") return { kind: "missing", message: "File is missing" };
      if (path === "ambiguous") return { kind: "ambiguous", query: "config.ts", line: 3 };
      return { kind: "file", path: "/work/valid.ts", line: null };
    },
  );
  await openFilesIn(session, ["valid", "missing", "ambiguous"]);
  expect(openTabsFor(session).map((tab) => tab.path)).toEqual([
    "/work/reading.ts",
    "/work/valid.ts",
  ]);
  expect(activePathFor(session)).toBe("/work/reading.ts");
  expect(omnibarRequest()).toMatchObject({ mode: "file", query: "config.ts", line: 3 });
  expect(deps.onOpenError).toHaveBeenCalledExactlyOnceWith("File is missing");
  controller.dispose();
});

it.each([
  false,
  true,
])("permits retired-source advancement only for its own deletion: %s", (sourceDeleted) => {
  const session = fakeSession(`source-deletion-${sourceDeleted}`);
  env.selected = session;
  const deps = dependencies(async () => true);
  const controller = createEditorController(deps);
  for (const install of env.installers) install(session);
  const review = session.feature("review") as unknown as FakeFeature;
  review.emit("changes", {
    label: "Review",
    files: ["/source.ts", "/next.ts"].map((path) => ({
      path,
      name: path,
      line: 1,
      added: 1,
      removed: 0,
      currentExists: true,
    })),
  });
  openTabFor(session, "/neighbor.ts", {});
  openTabFor(session, "/source.ts", {});
  const tab = tabOwnerFor(session, "/source.ts")!;
  tab.mount(controller.filePresenter(tab, () => undefined));
  const complete = tab.presentation!.captureReviewAdvance("/source.ts", "revertFile");
  const focus = deps.interaction.begin();
  closeTabFor(session, "/source.ts");
  expect(activePathFor(session)).toBe("/neighbor.ts");
  complete({ path: "/next.ts", line: 1 }, focus, { sourceDeleted, sourceHasReview: false });
  expect(activePathFor(session)).toBe(sourceDeleted ? "/next.ts" : "/neighbor.ts");
  controller.dispose();
});

it.each([
  ["keepFile", true, false],
  ["keepHunk", true, true],
  ["revertFile", true, false],
  ["revertFile", false, true],
] as const)("the ordinary presenter owns advancement for %s with remaining review=%s", (decision, sourceHasReview, advance) => {
  const session = fakeSession(`ordinary-${decision}`);
  env.selected = session;
  const deps = dependencies(async () => true);
  const controller = createEditorController(deps);
  for (const install of env.installers) install(session);
  (session.feature("review") as unknown as FakeFeature).emit("changes", {
    label: "Review",
    files: ["/source.ts", "/next.ts"].map((path) => ({
      path,
      name: path,
      line: 1,
      added: 1,
      removed: 0,
      currentExists: true,
    })),
  });
  openTabFor(session, "/source.ts", {});
  const tab = tabOwnerFor(session, "/source.ts")!;
  tab.mount(controller.filePresenter(tab, () => undefined));
  const complete = tab.presentation!.captureReviewAdvance("/source.ts", decision);
  complete({ path: "/next.ts", line: 1 }, deps.interaction.begin(), {
    sourceDeleted: false,
    sourceHasReview,
  });
  expect(activePathFor(session)).toBe(advance ? "/next.ts" : "/source.ts");
  controller.dispose();
});

it("routes a path-addressed Keep file completion through the selected review presenter", async () => {
  const session = fakeSession("review-header");
  env.selected = session;
  const controller = createEditorController(dependencies(async () => true));
  for (const install of env.installers) install(session);
  const review = session.feature("review") as unknown as FakeFeature;
  const path = "/work/change.ts";
  review.emit("changes", {
    label: "Review",
    files: [{ path, name: "change.ts", line: 1, added: 1, removed: 0, currentExists: true }],
  });
  review.emit("diff", {
    path,
    name: "change.ts",
    revision: "1",
    baseline: "before",
    baselineExists: true,
    current: "after",
    currentExists: true,
    acceptedBaseline: "before",
    acceptedBaselineExists: true,
    rejected: [],
  });
  openTabFor(session, "weavie:review", { kind: "review" });
  const complete = vi.fn();
  const captureReviewAdvance = vi.fn(() => complete);
  tabOwnerFor(session, "weavie:review")!.mount({
    text: true,
    capture: () => ({ state: null, text: { path, line: 1 } }),
    restore: async () => {},
    focus: () => {},
    actions: () => undefined,
    captureReviewAdvance,
  });
  const response = Promise.withResolvers<{
    sourceDeleted: boolean;
    sourceHasReview: boolean;
    next: { path: string; line: number };
  }>();
  review.requests.set("keepFile", () => response.promise);
  expect(controller.review.keepFile(session, path)).toBe(true);
  expect(captureReviewAdvance).toHaveBeenCalledExactlyOnceWith(path, "keepFile");
  expect(complete).not.toHaveBeenCalled();
  response.resolve({
    sourceDeleted: false,
    sourceHasReview: true,
    next: { path: "/work/next.ts", line: 1 },
  });
  await vi.waitFor(() => expect(complete).toHaveBeenCalledOnce());
  controller.dispose();
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
    captureReviewAdvance: () => () => {},
  });
  const secondCapture = vi.fn(() => ({ state: { top: 999 }, text: null }));
  tabOwnerFor(second, "weavie:review")!.mount({
    text: false,
    capture: secondCapture,
    restore: async () => {},
    focus: () => {},
    actions: () => undefined,
    captureReviewAdvance: () => () => {},
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
    captureReviewAdvance: () => () => {},
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
