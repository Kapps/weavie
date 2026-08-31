import { beforeEach, describe, expect, it, vi } from "vitest";

type Handler = (payload: never) => void;

const harness = vi.hoisted(() => ({
  installer: undefined as ((connection: unknown) => (() => void) | undefined) | undefined,
  splashDismissed: undefined as (() => void) | undefined,
}));
const notify = vi.hoisted(() => vi.fn());
const keyHint = vi.hoisted(() => vi.fn(() => " (Ctrl+Alt+E)"));

vi.mock("../bridge", () => ({
  registerHostFeature: (installer: (connection: unknown) => (() => void) | undefined) => {
    harness.installer = installer;
    return () => {};
  },
}));
vi.mock("../commands/key-hint", () => ({ keyHintInCatalog: keyHint }));
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

function install(isLocal: boolean): {
  cleanup: () => void;
  show: (payload: unknown) => void;
} {
  const handlers = new Map<string, Handler>();
  const cleanup =
    harness.installer?.({
      id: isLocal ? "local" : "remote:worker",
      isLocal,
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
  });

  it("waits for the splash and appends the local command catalog's effective shortcut", () => {
    const installed = install(true);

    installed.show(tip);
    expect(notify).not.toHaveBeenCalled();
    harness.splashDismissed?.();

    expect(keyHint).toHaveBeenCalledWith("local", "weavie.revise.selection");
    expect(notify).toHaveBeenCalledWith(
      "info",
      "Tip: Run Revise Selection (Ctrl+Alt+E). Rewrite selected code in one undo step.",
      "startup-tip:revise-selection",
    );
    installed.cleanup();
  });

  it("shows a later tip immediately when the splash is already gone and ignores repeats", () => {
    const installed = install(true);
    harness.splashDismissed?.();

    installed.show({ ...tip, commandId: null });
    installed.show({ ...tip, id: "another" });

    expect(notify).toHaveBeenCalledOnce();
    expect(notify).toHaveBeenCalledWith(
      "info",
      "Tip: Run Revise Selection. Rewrite selected code in one undo step.",
      "startup-tip:revise-selection",
    );
    expect(keyHint).not.toHaveBeenCalled();
    installed.cleanup();
  });

  it("ignores tips from remote hosts", () => {
    const installed = install(false);

    installed.show(tip);
    harness.splashDismissed?.();

    expect(notify).not.toHaveBeenCalled();
    installed.cleanup();
  });
});
