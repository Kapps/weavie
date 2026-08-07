import { beforeEach, describe, expect, it, vi } from "vitest";

type Handler = (payload: never) => void;

const bridge = vi.hoisted(() => ({
  hostInstaller: undefined as ((connection: unknown) => (() => void) | undefined) | undefined,
  sessionInstaller: undefined as ((session: unknown) => (() => void) | undefined) | undefined,
  phase: undefined as ((backendId: string, phase: string) => void) | undefined,
}));

vi.mock("../bridge", () => ({
  onBackendPhase: (handler: (backendId: string, phase: string) => void) => {
    bridge.phase = handler;
    return () => {};
  },
  registerHostFeature: (installer: (connection: unknown) => (() => void) | undefined) => {
    bridge.hostInstaller = installer;
    return () => {};
  },
  registerSessionFeature: (installer: (session: unknown) => (() => void) | undefined) => {
    bridge.sessionInstaller = installer;
    return () => {};
  },
}));

const notifications = vi.hoisted(() => ({
  notify: vi.fn(),
  clear: vi.fn(),
}));
vi.mock("../notify/notify", () => ({
  notify: notifications.notify,
  clearNotification: notifications.clear,
}));

const commands = vi.hoisted(() => ({
  keyHint: vi.fn(),
  run: vi.fn(),
}));
vi.mock("../commands/key-hint", () => ({ keyHintInCatalog: commands.keyHint }));
vi.mock("../commands/registry", () => ({
  runCommandFromCatalogWithFeedback: commands.run,
}));

await import("./intake");

function feature(): {
  channel: { on<T>(name: string, handler: (payload: T) => void): () => void };
  deliver: (name: string, payload: unknown) => void;
} {
  const handlers = new Map<string, Handler>();
  return {
    channel: {
      on: <T>(name: string, handler: (payload: T) => void) => {
        handlers.set(name, handler as Handler);
        return () => handlers.delete(name);
      },
    },
    deliver: (name, payload) => handlers.get(name)?.(payload as never),
  };
}

function installHost(
  backendId: string,
  notificationsFeature: ReturnType<typeof feature>["channel"],
): () => void {
  return (
    bridge.hostInstaller?.({
      id: backendId,
      host: { feature: () => notificationsFeature },
    }) ?? (() => {})
  );
}

describe("notification intake", () => {
  beforeEach(() => {
    notifications.notify.mockClear();
    notifications.clear.mockClear();
    commands.keyHint.mockReset();
    commands.run.mockReset();
  });

  it("isolates keyed notifications by backend", () => {
    const local = feature();
    const remote = feature();
    const disposeLocal = installHost("local", local.channel);
    const disposeRemote = installHost("remote:mobile", remote.channel);

    local.deliver("show", { level: "busy", message: "local", key: "message-operation:msg-1" });
    remote.deliver("show", { level: "busy", message: "remote", key: "message-operation:msg-1" });

    expect(notifications.notify).toHaveBeenNthCalledWith(
      1,
      "busy",
      "local",
      "backend:local:message-operation:msg-1",
    );
    expect(notifications.notify).toHaveBeenNthCalledWith(
      2,
      "busy",
      "remote",
      "backend:remote:mobile:message-operation:msg-1",
    );

    disposeLocal();
    disposeRemote();
  });

  it("clears only in-flight busy notifications when a backend drops", () => {
    const remote = feature();
    const dispose = installHost("remote:mobile", remote.channel);
    remote.deliver("show", { level: "busy", message: "slow", key: "message-operation:msg-1" });
    remote.deliver("show", { level: "error", message: "failed", key: "message-operation:msg-2" });

    bridge.phase?.("remote:mobile", "reconnecting");

    expect(notifications.clear).toHaveBeenCalledOnce();
    expect(notifications.clear).toHaveBeenCalledWith(
      "backend:remote:mobile:message-operation:msg-1",
    );
    dispose();
  });

  it("runs an action against the notification's backend and advertises its effective shortcut", () => {
    commands.keyHint.mockReturnValue(" (Ctrl+Alt+I)");
    const remote = feature();
    const dispose = installHost("remote:mobile", remote.channel);

    remote.deliver("show", {
      level: "info",
      message: "Allow automatic inference?",
      key: "inference-automatic-opt-in",
      action: {
        label: "Allow",
        commandId: "weavie.inference.enableAutomatic",
        argsJson: '{"source":"notification"}',
      },
    });

    expect(commands.keyHint).toHaveBeenCalledWith(
      "remote:mobile",
      "weavie.inference.enableAutomatic",
    );
    const action = notifications.notify.mock.calls[0]?.[3];
    expect(action?.label).toBe("Allow (Ctrl+Alt+I)");
    action?.run();
    expect(commands.run).toHaveBeenCalledWith("remote:mobile", "weavie.inference.enableAutomatic", {
      source: "notification",
    });
    dispose();
  });
});
