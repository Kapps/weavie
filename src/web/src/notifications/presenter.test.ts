import { beforeEach, describe, expect, it, vi } from "vitest";

const state = vi.hoisted(() => ({
  browserShell: false,
  activation: undefined as ((payload: unknown) => void) | undefined,
}));
const request = vi.hoisted(() => vi.fn());
const dispatchCommand = vi.hoisted(() => vi.fn());
const notify = vi.hoisted(() => vi.fn());

vi.mock("../bridge", () => ({
  hostConnection: () => ({
    host: {
      feature: () => ({
        request,
        on: (_name: string, handler: (payload: unknown) => void) => {
          state.activation = handler;
          return () => {};
        },
      }),
    },
  }),
  isBrowserHostedShell: () => state.browserShell,
  LOCAL_BACKEND_ID: "local",
  registerHostFeature: (register: (connection: unknown) => void) =>
    register({
      isLocal: true,
      host: {
        feature: () => ({
          request,
          on: (_name: string, handler: (payload: unknown) => void) => {
            state.activation = handler;
            return () => {};
          },
        }),
      },
    }),
}));
vi.mock("../commands/registry", () => ({ dispatchCommand }));
vi.mock("../commands/types", () => ({ CommandIds: { focusSession: "weavie.session.focus" } }));
vi.mock("../notify/notify", () => ({ notify }));

const focused = vi.fn();
vi.stubGlobal("document", { title: "Weavie" });
vi.stubGlobal("window", { addEventListener: vi.fn(), focus: focused });

class FakeNotification {
  static permission: NotificationPermission = "granted";
  static readonly instances: FakeNotification[] = [];
  static readonly requestPermission = vi.fn();

  readonly close = vi.fn();
  onclick: (() => void) | null = null;

  constructor(
    readonly title: string,
    readonly options: NotificationOptions,
  ) {
    FakeNotification.instances.push(this);
  }
}
vi.stubGlobal("Notification", FakeNotification);

const { presentOsNotification } = await import("./presenter");

const event = {
  backendId: "runner-a",
  slot: "feature-a",
  incarnation: "incarnation-a",
  label: "Feature A",
  kind: "needsInput" as const,
  body: "Needs your input.",
};

beforeEach(() => {
  state.browserShell = false;
  request.mockReset();
  dispatchCommand.mockReset();
  notify.mockReset();
  focused.mockReset();
  FakeNotification.instances.length = 0;
  FakeNotification.permission = "granted";
});

describe("OS notification presenter", () => {
  it("uses the local native host while preserving the remote session's exact address", async () => {
    request.mockImplementation(async (name: string) =>
      name === "permission" ? { permission: "granted" } : { shown: true },
    );

    presentOsNotification(event);

    await vi.waitFor(() => expect(request).toHaveBeenCalledTimes(2));
    expect(request).toHaveBeenNthCalledWith(1, "permission", {});
    expect(request).toHaveBeenNthCalledWith(2, "show", {
      backendId: "runner-a",
      address: { slot: "feature-a", incarnation: "incarnation-a" },
      label: "Feature A",
      kind: "needsInput",
    });
  });

  it("focuses the exact backend and incarnation from a native activation", () => {
    state.activation?.({
      backendId: "runner-b",
      address: { slot: "feature-b", incarnation: "incarnation-b" },
    });

    expect(dispatchCommand).toHaveBeenCalledWith("weavie.session.focus", {
      id: "feature-b",
      backendId: "runner-b",
      incarnation: "incarnation-b",
    });
  });

  it("uses the canonical body and exact incarnation for browser notifications", () => {
    state.browserShell = true;

    presentOsNotification(event);

    expect(FakeNotification.instances).toHaveLength(1);
    const notification = FakeNotification.instances[0];
    expect(notification?.title).toBe("Feature A");
    expect(notification?.options).toMatchObject({
      body: "Needs your input.",
      tag: "runner-a:feature-a",
      silent: true,
      renotify: true,
    });
    notification?.onclick?.();
    expect(focused).toHaveBeenCalledOnce();
    expect(dispatchCommand).toHaveBeenCalledWith("weavie.session.focus", {
      id: "feature-a",
      backendId: "runner-a",
      incarnation: "incarnation-a",
    });
    expect(notification?.close).toHaveBeenCalledOnce();
  });
});
