import { expect, it, vi } from "vitest";
import type { ClientSession } from "../bridge";
import type { EditorControllerDeps } from "./editor-controller";

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
const { createEditorController } = await import("./editor-controller");

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
  };
  const unloaded = {
    path: "/owner/lazy.ts",
    name: "lazy.ts",
    added: 1,
    removed: 0,
    line: 2,
  };
  review.emit("changes", { label: "owner", files: [file, unloaded] });
  review.emit("diff", {
    path: file.path,
    name: file.name,
    acceptedBaseline: "before",
    baseline: "before",
    current: "after",
  });

  expect(controller.review.revert(owner)).toBe(true);
  await vi.waitFor(() =>
    expect(review.published).toContainEqual({ name: "revertAll", payload: {} }),
  );
  expect(confirm).toHaveBeenCalledWith(expect.objectContaining({ title: "Revert all changes?" }));
});
