import { describe, expect, it, vi } from "vitest";
import { backgroundStopRequest, backgroundWorkOf, guardBackgroundWork } from "./background-guard";

const work = [{ name: "sleep 30", type: "shell", state: "running", startedAtMs: 1 }];
const refusal = { ok: false, error: "still running", data: { backgroundWork: work } };

describe("background work guard", () => {
  it("recognises only a refusal that lists background work", () => {
    expect(backgroundWorkOf(refusal)).toEqual(work);
    expect(backgroundWorkOf({ ok: false, error: "other" })).toBeNull();
    expect(backgroundWorkOf({ ok: true, data: { backgroundWork: work } })).toBeNull();
  });

  it("re-runs with consent when the user closes anyway, and keeps the session otherwise", async () => {
    const run = vi.fn(async () => ({ ok: true }));
    const closing = guardBackgroundWork(Promise.resolve(refusal), { id: "s1" }, run);
    await vi.waitFor(() => expect(backgroundStopRequest()?.work).toEqual(work));
    backgroundStopRequest()!.settle(true);
    expect(await closing).toEqual({ ok: true });
    expect(run).toHaveBeenCalledWith({ id: "s1", stopBackgroundWork: true });

    const keeping = guardBackgroundWork(Promise.resolve(refusal), undefined, run);
    await vi.waitFor(() => expect(backgroundStopRequest()).not.toBeNull());
    backgroundStopRequest()!.settle(false);
    expect(await keeping).toEqual({ ok: false, cancelled: true });
    expect(run).toHaveBeenCalledTimes(1);
  });

  it("passes every other result through untouched", async () => {
    const run = vi.fn();
    expect(await guardBackgroundWork(Promise.resolve({ ok: true }), undefined, run)).toEqual({
      ok: true,
    });
    expect(run).not.toHaveBeenCalled();
  });
});
