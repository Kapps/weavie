import { beforeEach, describe, expect, it, vi } from "vitest";

type Handler = (payload: never) => void;

const harness = vi.hoisted(() => ({
  installer: undefined as ((connection: unknown) => (() => void) | undefined) | undefined,
  splashDismissed: undefined as (() => void) | undefined,
  commandsChanged: undefined as (() => void) | undefined,
}));
const notify = vi.hoisted(() => vi.fn());
const keyHint = vi.hoisted(() => vi.fn(() => " (Ctrl+Shift+E)"));
const findCommand = vi.hoisted(() =>
  vi.fn((_backendId: string, id: string): { id: string; title: string } | undefined => ({
    id,
    title: "Revise Selection",
  })),
);
const runCommand = vi.hoisted(() => vi.fn());

vi.mock("../bridge", () => ({
  registerHomeFeature: (installer: (connection: unknown) => (() => void) | undefined) => {
    harness.installer = installer;
    return () => {};
  },
}));
vi.mock("../commands/key-hint", () => ({ keyHintInCatalog: keyHint }));
vi.mock("../commands/registry", () => ({
  findCommandInCatalog: findCommand,
  onCommandsChanged: (listener: () => void) => {
    harness.commandsChanged = listener;
    return () => {
      harness.commandsChanged = undefined;
    };
  },
  runCommandFromCatalogWithFeedback: runCommand,
}));
vi.mock("../notify/notify", () => ({ notify }));
vi.mock("../splash", () => ({
  onSplashDismissed: (listener: () => void) => {
    harness.splashDismissed = listener;
    return () => {
      harness.splashDismissed = undefined;
    };
  },
}));

await import("./startup-tip");

function install(): {
  cleanup: () => void;
  show: (payload: unknown) => void;
} {
  const handlers = new Map<string, Handler>();
  const cleanup =
    harness.installer?.({
      id: "local",
      host: {
        feature: () => ({
          on: (name: string, handler: Handler) => {
            handlers.set(name, handler);
            return () => handlers.delete(name);
          },
        }),
      },
    }) ?? (() => {});
  return {
    cleanup,
    show: (payload) => handlers.get("show")?.(payload as never),
  };
}

const tip = {
  id: "revise-selection",
  lead: "Run Revise Selection",
  commandId: "weavie.revise.selection",
  detail: "Rewrite selected code in one undo step.",
};

describe("startup tip intake", () => {
  beforeEach(() => {
    harness.splashDismissed = undefined;
    notify.mockClear();
    keyHint.mockClear();
    findCommand.mockClear();
    runCommand.mockClear();
  });

  it("waits for the splash, appends the effective shortcut, and offers the command as a button", () => {
    const installed = install();

    installed.show(tip);
    expect(notify).not.toHaveBeenCalled();
    harness.splashDismissed?.();

    expect(keyHint).toHaveBeenCalledWith("local", "weavie.revise.selection");
    expect(notify).toHaveBeenCalledWith(
      "info",
      "Tip: Run Revise Selection (Ctrl+Shift+E). Rewrite selected code in one undo step.",
      "startup-tip:revise-selection",
      { label: "Revise Selection", run: expect.any(Function), passing: true },
    );
    notify.mock.calls[0]?.[3].run();
    expect(runCommand).toHaveBeenCalledWith("local", "weavie.revise.selection");
    installed.cleanup();
  });

  it("omits the shortcut for an unbound command but keeps its button", () => {
    keyHint.mockReturnValueOnce("");
    const installed = install();
    harness.splashDismissed?.();

    installed.show(tip);

    expect(notify).toHaveBeenCalledWith(
      "info",
      "Tip: Run Revise Selection. Rewrite selected code in one undo step.",
      "startup-tip:revise-selection",
      { label: "Revise Selection", run: expect.any(Function), passing: true },
    );
    installed.cleanup();
  });

  it("holds a command tip until the catalog names its command", () => {
    findCommand.mockReturnValueOnce(undefined);
    const installed = install();
    harness.splashDismissed?.();

    installed.show(tip);
    expect(notify).not.toHaveBeenCalled();
    harness.commandsChanged?.();

    expect(notify).toHaveBeenCalledOnce();
    installed.cleanup();
  });

  it("shows a later tip immediately when the splash is already gone and ignores repeats", () => {
    const installed = install();
    harness.splashDismissed?.();

    installed.show({ ...tip, commandId: null });
    installed.show({ ...tip, id: "another" });

    expect(notify).toHaveBeenCalledOnce();
    expect(notify).toHaveBeenCalledWith(
      "info",
      "Tip: Run Revise Selection. Rewrite selected code in one undo step.",
      "startup-tip:revise-selection",
      undefined,
    );
    expect(keyHint).not.toHaveBeenCalled();
    installed.cleanup();
  });
});
