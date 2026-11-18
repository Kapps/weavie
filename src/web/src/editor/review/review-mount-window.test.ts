import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../../editor-options", () => ({}));

import { mountedSpan, type VisibleRange, watchRest } from "./review-mount-window";

const visible = (startIndex: number, endIndex: number): VisibleRange => ({
  startIndex,
  endIndex,
  overscan: 1,
  count: 20,
});

describe("mountedSpan", () => {
  it("mounts only the overscan before the review has rested", () => {
    expect(mountedSpan(visible(5, 5), 0, 2, undefined)).toEqual([4, 6]);
  });

  it("reaches one file further on each side per rested step, within the file list", () => {
    expect(mountedSpan(visible(5, 5), 1, 2, [4, 6])).toEqual([3, 7]);
    expect(mountedSpan(visible(0, 0), 2, 2, [0, 1])).toEqual([0, 3]);
    expect(mountedSpan(visible(19, 19), 2, 2, [18, 19])).toEqual([16, 19]);
  });

  it("keeps rested files mounted through a scroll into them", () => {
    expect(mountedSpan(visible(6, 6), 0, 2, [2, 8])).toEqual([2, 8]);
    expect(mountedSpan(visible(7, 7), 0, 2, [2, 8])).toEqual([2, 8]);
  });

  it("unmounts a file only once it is two past the reach, or the review rests again", () => {
    expect(mountedSpan(visible(8, 8), 0, 2, [2, 8])).toEqual([3, 9]);
    expect(mountedSpan(visible(7, 7), 2, 2, [2, 8])).toEqual([4, 10]);
  });

  it("drops the old files on a jump without mounting the files between", () => {
    expect(mountedSpan(visible(9, 9), 0, 2, [2, 5])).toEqual([8, 10]);
  });

  it("is the plain overscan with no reach", () => {
    expect(mountedSpan(visible(6, 6), 0, 0, [4, 6])).toEqual([5, 7]);
  });
});

describe("watchRest", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => vi.useRealTimers());

  function watch(limit: number) {
    let scrolled: () => void = () => {};
    const levels: number[] = [];
    const dispose = watchRest(
      {
        onScroll: (listener) => {
          scrolled = () => listener(true);
          return () => {};
        },
      },
      limit,
      (level) => levels.push(level),
    );
    return { dispose, levels, scroll: () => scrolled() };
  }

  it("rests one level deeper per step, up to the limit", () => {
    const rest = watch(2);
    vi.advanceTimersByTime(199);
    expect(rest.levels).toEqual([0]);
    vi.advanceTimersByTime(1);
    expect(rest.levels).toEqual([0, 1]);
    vi.advanceTimersByTime(150);
    expect(rest.levels).toEqual([0, 1, 2]);
    vi.advanceTimersByTime(1000);
    expect(rest.levels).toEqual([0, 1, 2]);
  });

  it("starts over on every scroll, and stops with its disposal", () => {
    const rest = watch(2);
    vi.advanceTimersByTime(300);
    rest.scroll();
    vi.advanceTimersByTime(199);
    expect(rest.levels).toEqual([0, 1, 0]);
    vi.advanceTimersByTime(1);
    expect(rest.levels).toEqual([0, 1, 0, 1]);
    rest.dispose();
    vi.advanceTimersByTime(1000);
    expect(rest.levels).toEqual([0, 1, 0, 1]);
  });
});
