import type { Range } from "@tanstack/solid-virtual";
import { createRoot } from "solid-js";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const options = vi.hoisted(() => ({
  reviewScreensAhead: 2,
  changed: (_options: { reviewScreensAhead: number }): void => {},
}));
vi.mock("../../editor-options", () => ({
  currentEditorOptions: () => options,
  onEditorOptionsChanged: (handler: typeof options.changed) => {
    options.changed = handler;
    return () => {};
  },
}));

import { createMountWindow, mountedSpan } from "./review-mount-window";

const visible = (startIndex: number, endIndex: number): Range => ({
  startIndex,
  endIndex,
  overscan: 1,
  count: 20,
});

const reach = (before: number, after: number) => ({ before, after });

describe("mountedSpan", () => {
  it("reaches one file further on each side per rested step, within the file list", () => {
    expect(mountedSpan(visible(5, 5), 1, reach(2, 2), [4, 6]).span).toEqual([3, 7]);
    expect(mountedSpan(visible(0, 0), 2, reach(2, 2), [0, 1]).span).toEqual([0, 3]);
    expect(mountedSpan(visible(19, 19), 2, reach(2, 2), [18, 19]).span).toEqual([16, 19]);
  });

  it("reaches each side as far as its own count", () => {
    expect(mountedSpan(visible(5, 5), 3, reach(1, 3), [4, 6]).span).toEqual([3, 9]);
  });

  it("keeps what was mounted, past the reach too", () => {
    expect(mountedSpan(visible(7, 7), 0, reach(2, 2), [2, 8]).span).toEqual([2, 8]);
  });

  it("mounts only the overscan when nothing mounted carries over, however long the review rested", () => {
    expect(mountedSpan(visible(5, 5), 2, reach(2, 2), undefined)).toEqual({
      span: [4, 6],
      fresh: true,
    });
    expect(mountedSpan(visible(9, 9), 2, reach(2, 2), [2, 5])).toEqual({
      span: [8, 10],
      fresh: true,
    });
    expect(mountedSpan(visible(5, 5), 2, reach(2, 2), [4, 6]).fresh).toBe(false);
  });

  it("is the plain overscan with no reach", () => {
    expect(mountedSpan(visible(6, 6), 0, reach(0, 0), [5, 7]).span).toEqual([5, 7]);
  });
});

describe("createMountWindow", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    vi.useRealTimers();
    options.reviewScreensAhead = 2;
  });

  // A review of twenty files, each `height(index)` tall, on a `screen` px viewport; `mount(at)` shows file `at`.
  function review(height: (index: number) => number = () => 1000) {
    let files = Array.from({ length: 20 }, (_, index) => `file${index}`);
    let shown = false;
    let screen = 1000;
    return createRoot((dispose) => {
      const window = createMountWindow({
        keyAt: (index) => files[index],
        rendered: () => shown,
        sizeAt: height,
        screen: () => screen,
      });
      const mount = (at: number): number[] => {
        const mounted = window.extractor()(visible(at, at));
        shown = true;
        return mounted;
      };
      return {
        dispose,
        mount,
        hide: (): void => {
          shown = false;
        },
        replace: (next: (files: string[]) => string[]): void => {
          files = next(files);
        },
        scrolled: window.scrolled,
        corrected: window.corrected,
        changed: window.changed,
        resize: (height: number): void => {
          screen = height;
        },
      };
    });
  }

  it("mounts one more file on each side per step of rest", () => {
    const page = review();
    expect(page.mount(5)).toEqual([4, 5, 6]);
    vi.advanceTimersByTime(199);
    expect(page.mount(5)).toEqual([4, 5, 6]);
    vi.advanceTimersByTime(1);
    expect(page.mount(5)).toEqual([3, 4, 5, 6, 7]);
    vi.advanceTimersByTime(50);
    expect(page.mount(5)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    page.dispose();
  });

  it("mounts as many short files as fill the screens ahead, up to eight a side", () => {
    const short = review(() => 400);
    short.mount(10);
    vi.advanceTimersByTime(200 + 4 * 50);
    expect(short.mount(10)).toEqual([4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
    short.dispose();
    const tiny = review(() => 100);
    tiny.mount(10);
    vi.advanceTimersByTime(200 + 7 * 50);
    expect(tiny.mount(10)).toEqual(Array.from({ length: 19 }, (_, index) => index + 1));
    tiny.dispose();
  });

  it("unmounts nothing through a scroll, then lets go of a file per step of rest", () => {
    const page = review();
    page.mount(5);
    vi.advanceTimersByTime(250);
    page.mount(5);
    for (let at = 6; at <= 12; at++) {
      page.scrolled(at * 100);
      expect(page.mount(at)[0]).toBe(2);
    }
    vi.advanceTimersByTime(200);
    expect(page.mount(12)).toEqual([3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14]);
    vi.advanceTimersByTime(50);
    expect(page.mount(12)).toEqual([4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15]);
    for (let step = 0; step < 5; step++) {
      vi.advanceTimersByTime(50);
      page.mount(12);
    }
    expect(page.mount(12)).toEqual([9, 10, 11, 12, 13, 14, 15]);
    expect(vi.getTimerCount()).toBe(0);
    page.dispose();
  });

  it("reaches each side by its own files' heights", () => {
    const page = review((index) => (index < 10 ? 250 : 3000));
    page.mount(10);
    vi.advanceTimersByTime(200 + 7 * 50);
    // Eight short files above; below, the two files every side keeps however tall.
    expect(page.mount(10)).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]);
    page.dispose();
  });

  it("steps on when files measure shorter than estimated after the rest has settled", () => {
    let height = 1000;
    const page = review(() => height);
    page.mount(5);
    vi.advanceTimersByTime(200 + 8 * 50);
    expect(page.mount(5)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    expect(vi.getTimerCount()).toBe(0);
    height = 250;
    page.changed();
    vi.advanceTimersByTime(6 * 50);
    expect(page.mount(5)).toEqual(Array.from({ length: 15 }, (_, index) => index));
    expect(vi.getTimerCount()).toBe(0);
    page.dispose();
  });

  it("keeps two files a side before the screen has a height", () => {
    const page = review();
    page.resize(0);
    page.mount(5);
    vi.advanceTimersByTime(250);
    expect(page.mount(5)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    page.dispose();
  });

  it("keeps resting through a scroll that only corrects for a file sized above", () => {
    const page = review();
    page.scrolled(500);
    page.mount(5);
    vi.advanceTimersByTime(200);
    page.corrected(40);
    page.scrolled(540);
    vi.advanceTimersByTime(50);
    expect(page.mount(5)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    page.scrolled(600);
    expect(page.mount(5)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    vi.advanceTimersByTime(199);
    expect(page.mount(5)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    page.dispose();
  });

  it("holds the rested files through a scroll and mounts nothing new until it rests again", () => {
    const page = review();
    page.mount(5);
    vi.advanceTimersByTime(250);
    page.mount(5);
    page.scrolled(100);
    expect(page.mount(6)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    page.scrolled(200);
    expect(page.mount(7)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    vi.advanceTimersByTime(250);
    expect(page.mount(7)).toEqual([4, 5, 6, 7, 8, 9, 10]);
    page.dispose();
  });

  it("starts the rest over when the position changes, and not when it is reported unchanged", () => {
    const page = review();
    page.mount(5);
    vi.advanceTimersByTime(199);
    page.scrolled(100);
    vi.advanceTimersByTime(199);
    page.scrolled(100);
    expect(page.mount(5)).toEqual([4, 5, 6]);
    vi.advanceTimersByTime(1);
    expect(page.mount(5)).toEqual([3, 4, 5, 6, 7]);
    page.dispose();
  });

  it("mounts only the overscan after a jump from rest, then rests up again", async () => {
    const page = review();
    page.mount(5);
    vi.advanceTimersByTime(250);
    page.mount(5);
    expect(page.mount(15)).toEqual([14, 15, 16]);
    await Promise.resolve();
    expect(page.mount(15)).toEqual([14, 15, 16]);
    vi.advanceTimersByTime(250);
    expect(page.mount(15)).toEqual([12, 13, 14, 15, 16, 17, 18]);
    page.dispose();
  });

  it("mounts only the overscan when a rested page comes back with nothing rendered", async () => {
    const page = review();
    page.mount(5);
    vi.advanceTimersByTime(250);
    page.mount(5);
    page.hide();
    expect(page.mount(5)).toEqual([4, 5, 6]);
    await Promise.resolve();
    vi.advanceTimersByTime(200);
    expect(page.mount(5)).toEqual([3, 4, 5, 6, 7]);
    page.dispose();
  });

  it("mounts only the overscan of a replaced list once, then rests on it", async () => {
    const page = review();
    page.mount(5);
    vi.advanceTimersByTime(250);
    page.mount(5);
    page.replace((files) => files.map((file) => `other/${file}`));
    expect(page.mount(5)).toEqual([4, 5, 6]);
    await Promise.resolve();
    vi.advanceTimersByTime(250);
    expect(page.mount(5)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    page.dispose();
  });

  it("carries the mounted files over when the list shifts them to other indexes", () => {
    const page = review();
    page.mount(5);
    vi.advanceTimersByTime(250);
    expect(page.mount(5)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    page.replace((files) => files.slice(1));
    page.scrolled(100);
    expect(page.mount(4)).toEqual([1, 2, 3, 4, 5, 6, 7]);
    page.dispose();
  });

  it("does not rest again once disposed", async () => {
    const page = review();
    page.mount(5);
    vi.advanceTimersByTime(250);
    page.hide();
    page.mount(5);
    page.dispose();
    await Promise.resolve();
    expect(vi.getTimerCount()).toBe(0);
  });

  it("follows the setting live, and mounts nothing ahead at 0", () => {
    const page = review();
    page.mount(5);
    vi.advanceTimersByTime(250);
    expect(page.mount(5)).toHaveLength(7);
    options.changed({ reviewScreensAhead: 0 });
    expect(page.mount(5)).toEqual([4, 5, 6]);
    vi.advanceTimersByTime(1000);
    expect(page.mount(5)).toEqual([4, 5, 6]);
    page.dispose();
  });
});
