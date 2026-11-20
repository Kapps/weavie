import { beforeEach, expect, it, vi } from "vitest";
import { confirm, confirmationRequest, settleConfirmation } from "../chrome/confirmation";
import { invokeHostSessionCommand } from "./session-recovery";
import { CommandIds, type CommandResult } from "./types";

const invoke = vi.hoisted(() => vi.fn<(...args: unknown[]) => Promise<CommandResult>>());
vi.mock("../bridge", () => ({ invokeSessionCommandOnBackend: invoke }));
vi.mock("../chrome/session-store", () => ({
  trackSessionCommand: <T>(_backend: string, _id: string, run: () => Promise<T>) => run(),
}));

beforeEach(() => {
  invoke.mockReset();
  invoke.mockResolvedValueOnce({
    ok: false,
    error: "Session load failed",
    data: { corruptReviewSlot: "parked", sessionName: "design", reason: "Missing text." },
  });
});

it("keeps a pending confirmation open and sends no retry on cancellation", async () => {
  const discard = confirm({
    title: "Discard file?",
    body: "Unsaved changes",
    confirmLabel: "Discard",
  });
  const load = invokeHostSessionCommand("remote", CommandIds.loadSession, { id: "parked" });
  await Promise.resolve();
  expect(confirmationRequest()?.title).toBe("Discard file?");
  settleConfirmation(true);
  expect(await discard).toBe(true);
  expect(confirmationRequest()?.body).toContain("Missing text.");
  settleConfirmation(false);
  expect(await load).toEqual({ ok: false, cancelled: true });
  expect(invoke).toHaveBeenCalledTimes(1);
});

it.each([
  [CommandIds.loadSession, false],
  [CommandIds.newSession, true],
] as const)("recovers on the original host for %s", async (command, activate) => {
  const address = { slot: "parked", incarnation: "new" };
  invoke.mockResolvedValueOnce({ ok: true, data: { address } });
  const load = invokeHostSessionCommand("remote", command, { id: "parked" });
  await vi.waitFor(() => expect(confirmationRequest()?.title).toContain("design"));
  settleConfirmation(true);
  expect(await load).toEqual({
    ok: true,
    data: activate ? { address, activateSession: true } : { address },
  });
  expect(invoke).toHaveBeenLastCalledWith("remote", CommandIds.loadSession, {
    id: "parked",
    clearCorruptReview: true,
  });
});
