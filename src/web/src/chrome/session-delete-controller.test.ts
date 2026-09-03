import { beforeEach, describe, expect, it, vi } from "vitest";
import type { CommandResult } from "../commands/types";
import type { ClientSession } from "../messaging/host-connection";

const env = vi.hoisted(() => ({
  clientSession: vi.fn(),
  dispatch: vi.fn(),
  waitForClientSession: vi.fn(),
}));

vi.mock("../bridge", () => ({
  clientSession: env.clientSession,
  waitForClientSession: env.waitForClientSession,
}));

vi.mock("../commands/registry", () => ({
  dispatchCommandFromCatalog: env.dispatch,
}));

const { createSessionDeleteController } = await import("./session-delete-controller");

const owner = {} as ClientSession;

function preview(id: string): unknown {
  return {
    revision: `revision-${id}`,
    label: id,
    removesCheckout: true,
    worktree: {
      state: "clean",
      branchless: false,
      changedFiles: [],
      changedCount: 0,
    },
    drafts: [{ path: `/scratch/${id}`, name: `Untitled-${id}` }],
  };
}

function deferred<T>(): {
  promise: Promise<T>;
  resolve: (value: T) => void;
} {
  let resolve = (_value: T): void => {};
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

function previewDispatch(
  backendId: string,
  command: string,
  args: unknown,
): Promise<CommandResult> {
  const operation = (args as { operation?: string }).operation;
  if (operation === "preview") {
    return Promise.resolve({ ok: true, data: preview((args as { id: string }).id) });
  }
  throw new Error(`Unexpected command ${backendId}:${command}:${operation}`);
}

beforeEach(() => {
  env.clientSession.mockReset().mockReturnValue(owner);
  env.dispatch.mockReset().mockImplementation(previewDispatch);
  env.waitForClientSession.mockReset().mockResolvedValue(owner);
});

describe("createSessionDeleteController", () => {
  it("does not let an old confirmation close a newer dialog", async () => {
    const confirming = deferred<CommandResult>();
    env.dispatch.mockImplementation((backendId, command, args) => {
      if ((args as { operation?: string }).operation === "confirm") {
        return confirming.promise;
      }
      return previewDispatch(backendId, command, args);
    });
    const controller = createSessionDeleteController({
      editor: { saveScratchFor: vi.fn() },
      onError: vi.fn(),
    });
    await controller.open("A", "local");

    const pending = controller.confirm();
    await vi.waitFor(() => expect(controller.request()?.busy).toBe(true));
    await controller.open("B", "local");
    confirming.resolve({ ok: true });
    await pending;

    expect(controller.request()).toMatchObject({ id: "B", busy: false });
  });

  it("does not let an old Save As completion refresh a newer dialog", async () => {
    const saving = deferred<{ status: "saved"; savedPath: string }>();
    const saveScratchFor = vi.fn(() => saving.promise);
    const controller = createSessionDeleteController({
      editor: { saveScratchFor },
      onError: vi.fn(),
    });
    await controller.open("A", "local");

    const pending = controller.saveDrafts();
    await vi.waitFor(() => expect(saveScratchFor).toHaveBeenCalledOnce());
    await controller.open("B", "local");
    saving.resolve({ status: "saved", savedPath: "/workspace/A.txt" });
    await pending;

    expect(controller.request()).toMatchObject({ id: "B", busy: false });
    expect(env.dispatch).toHaveBeenCalledTimes(2);
  });

  it("does not start Save As when another dialog supersedes a pending session load", async () => {
    const waitingForOwner = deferred<ClientSession>();
    env.clientSession.mockReturnValue(undefined);
    env.waitForClientSession.mockReturnValue(waitingForOwner.promise);
    env.dispatch.mockImplementation((backendId, command, args) => {
      if (command.endsWith("session.load")) {
        return Promise.resolve({
          ok: true,
          data: { address: { slot: "A", incarnation: "incarnation-A" } },
        });
      }
      return previewDispatch(backendId, command, args);
    });
    const saveScratchFor = vi.fn();
    const controller = createSessionDeleteController({
      editor: { saveScratchFor },
      onError: vi.fn(),
    });
    await controller.open("A", "local");

    const pending = controller.saveDrafts();
    await vi.waitFor(() => expect(env.waitForClientSession).toHaveBeenCalledOnce());
    await controller.open("B", "local");
    waitingForOwner.resolve(owner);
    await pending;

    expect(saveScratchFor).not.toHaveBeenCalled();
    expect(controller.request()).toMatchObject({ id: "B", busy: false });
  });
});
