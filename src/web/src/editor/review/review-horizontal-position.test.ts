import { describe, expect, it, vi } from "vitest";
import { ReviewHorizontalPositions } from "./review-horizontal-position";

describe("file-owned horizontal positions", () => {
  it("captures horizontal-only changes immediately and keeps snapshots stable for no-ops", () => {
    const capture = vi.fn();
    const positions = new ReviewHorizontalPositions(capture);
    const file = positions.forPath("/file.ts");
    file.set(230);
    const snapshot = positions.snapshot();
    file.set(230.9);
    expect(capture).toHaveBeenCalledOnce();
    expect(positions.snapshot()).toBe(snapshot);
    file.set(0);
    expect(capture).toHaveBeenCalledTimes(2);
    expect(positions.snapshot()).toEqual({});
    expect(snapshot).toEqual({ "/file.ts": 230 });
  });

  it("clamps an old snapshot to the measured range before notifying adapters", () => {
    const capture = vi.fn();
    const positions = new ReviewHorizontalPositions(capture);
    const file = positions.forPath("/file.ts");
    const range = file.bindRange();
    file.set(900);
    const saved = positions.snapshot();
    range.update(300);
    file.set(100);
    const painted: number[] = [];
    file.subscribe(() => painted.push(file.get()));
    positions.restore(saved);
    expect(painted).toEqual([300]);
    expect(file.get()).toBe(300);
    expect(saved).toEqual({ "/file.ts": 900 });
  });

  it("does not let inactive passive preparation clamp the live editor", () => {
    const positions = new ReviewHorizontalPositions(vi.fn());
    const file = positions.forPath("/file.ts");
    const passive = file.bindRange();
    passive.update(1000);
    file.set(800);
    const live = file.bindRange();
    expect(file.get()).toBe(800);
    live.update(1200);
    passive.update(100);
    file.set(1100);
    expect(file.get()).toBe(1100);
    positions.restore({ "/file.ts": 2000 });
    expect(file.get()).toBe(1200);
    passive.update(900);
    live.dispose();
    expect(file.get()).toBe(900);
    live.dispose();
    expect(() => live.update(2000)).toThrow("no longer belongs");
  });

  it("preserves unmeasured restore intent and releases non-current ranges independently", () => {
    const positions = new ReviewHorizontalPositions(vi.fn());
    positions.restore({ "/file.ts": 800 });
    const file = positions.forPath("/file.ts");
    const passive = file.bindRange();
    expect(file.get()).toBe(800);
    const live = file.bindRange();
    live.update(900);
    passive.update(0);
    passive.dispose();
    expect(file.get()).toBe(800);
    live.dispose();
    file.set(2000);
    expect(file.get()).toBe(2000);
  });
  it("shares offsets between adapters without reallocating snapshots during vertical scrolling", () => {
    const positions = new ReviewHorizontalPositions(vi.fn());
    const passive = positions.forPath("/work/a.ts");
    const live = positions.forPath("/work/a.ts");
    const changed = vi.fn();
    const release = live.subscribe(changed);
    passive.set(200.8);
    const saved = positions.snapshot();
    expect(live.get()).toBe(200);
    passive.set(200.1);
    expect(positions.snapshot()).toBe(saved);
    expect(changed).toHaveBeenCalledOnce();
    positions.forPath("/work/b.ts").set(90);
    expect(saved).toEqual({ "/work/a.ts": 200 });
    expect(live.get()).toBe(200);
    release();
    passive.set(0);
    expect(changed).toHaveBeenCalledOnce();
  });

  it("restores serializable positions before a file has a measured range", () => {
    const first = new ReviewHorizontalPositions(vi.fn());
    first.forPath("/long.ts").set(12_340);
    const next = new ReviewHorizontalPositions(vi.fn());
    const file = next.forPath("/long.ts");
    const changed = vi.fn();
    file.subscribe(changed);
    next.restore(JSON.parse(JSON.stringify(first.snapshot())));
    expect(file.get()).toBe(12_340);
    expect(changed).toHaveBeenCalledOnce();
    file.set(-10);
    expect(file.get()).toBe(0);
    expect(first.forPath("/long.ts").get()).toBe(12_340);
  });
});
