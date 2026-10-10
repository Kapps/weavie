import { createSignal } from "solid-js";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { CommandIds } from "../commands/types";
import type { AgentBackgroundItem } from "./agent-background-store";

const [running, setRunning] = createSignal<AgentBackgroundItem[]>([]);
vi.mock("./agent-background-store", () => ({ runningBackgroundOf: () => running() }));
const { backgroundRefusal, backgroundStopRequest, guardBackgroundWork, stopQuestionMoot } =
  await import("./background-guard");

const session = { slot: "s1", incarnation: "i1" };
const item = { id: "t1", name: "sleep 30", type: "shell", state: "running" } as AgentBackgroundItem;
const refusal = {
  ok: false,
  error: "still running",
  data: { session, backgroundWork: [{ name: "sleep 30" }] },
};
const guard = (run: (args: unknown) => Promise<{ ok: boolean }>, args: unknown) =>
  guardBackgroundWork(CommandIds.unloadSession, "local", Promise.resolve(refusal), args, run);

describe("background work guard", () => {
  beforeEach(() => setRunning([item]));

  it("recognises only a refusal that names its session and lists background work", () => {
    expect(backgroundRefusal(refusal)).toEqual(refusal.data);
    expect(backgroundRefusal({ ok: false, error: "other" })).toBeNull();
    expect(backgroundRefusal({ ...refusal, ok: true })).toBeNull();
  });

  it("names the action and re-runs with consent on stop, and keeps the work otherwise", async () => {
    const run = vi.fn(async () => ({ ok: true }));
    const stopping = guard(run, { id: "s1" });
    await vi.waitFor(() => expect(backgroundStopRequest()?.confirmLabel).toBe("Stop and unload"));
    expect(backgroundStopRequest()!.refused).toEqual(refusal.data.backgroundWork);
    expect(backgroundStopRequest()!.running()).toEqual([item]);
    backgroundStopRequest()!.settle("stop");
    expect(await stopping).toEqual({ ok: true });
    expect(run).toHaveBeenCalledWith({ id: "s1", stopBackgroundWork: true });

    const keeping = guard(run, undefined);
    await vi.waitFor(() => expect(backgroundStopRequest()).not.toBeNull());
    backgroundStopRequest()!.settle("keep");
    expect(await keeping).toEqual({ ok: false, cancelled: true });
    expect(run).toHaveBeenCalledTimes(1);
  });

  it("re-runs without consent when the work finished, asking again about anything new", async () => {
    const run = vi
      .fn<(args: unknown) => Promise<typeof refusal | { ok: boolean }>>()
      .mockResolvedValueOnce(refusal)
      .mockResolvedValueOnce({ ok: true });
    const closing = guard(run, { id: "s1" });
    await vi.waitFor(() => expect(backgroundStopRequest()).not.toBeNull());
    backgroundStopRequest()!.settle("finished");
    await vi.waitFor(() => expect(run).toHaveBeenCalledTimes(1));
    expect(run).toHaveBeenLastCalledWith({ id: "s1" });
    await vi.waitFor(() => expect(backgroundStopRequest()).not.toBeNull());
    backgroundStopRequest()!.settle("finished");
    expect(await closing).toEqual({ ok: true });
    expect(run).toHaveBeenLastCalledWith({ id: "s1" });
  });

  it("is moot once its session is gone, even before the live list caught up", () => {
    expect(stopQuestionMoot(null, false)).toBe(true);
    expect(stopQuestionMoot(null, true)).toBe(true);
    expect(stopQuestionMoot([], false)).toBe(false);
    expect(stopQuestionMoot([], true)).toBe(true);
    expect(stopQuestionMoot([item], true)).toBe(false);
  });

  it("passes every other result through untouched", async () => {
    const run = vi.fn();
    expect(
      await guardBackgroundWork(
        CommandIds.unloadSession,
        "local",
        Promise.resolve({ ok: true }),
        undefined,
        run,
      ),
    ).toEqual({ ok: true });
    expect(run).not.toHaveBeenCalled();
  });
});
