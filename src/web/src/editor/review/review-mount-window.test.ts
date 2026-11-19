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

import { createMountWindow } from "./review-mount-window";

const span = (from: number, to: number): number[] =>
  Array.from({ length: to - from + 1 }, (_, index) => from + index);

describe("createMountWindow", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    vi.useRealTimers();
    options.reviewScreensAhead = 2;
  });

  // `count` files, each `height(index)` tall, on a `screen` px viewport; `mount(at)` shows file `at`.
  function review(height: (index: number) => number = () => 1000, count = 20) {
    let files = Array.from({ length: count }, (_, index) => `file${index}`);
    let shown = false;
    let screen = 1000;
    return createRoot((dispose) => {
      const window = createMountWindow({
        keyAt: (index) => files[index],
        rendered: () => shown,
        sizeAt: (index) => height(index),
        screen: () => screen,
      });
      const visible = (at: number): Range => ({
        startIndex: at,
        endIndex: at,
        overscan: 1,
        count: files.length,
      });
      const mount = (at: number): number[] => {
        const mounted = window.extractor()(visible(at));
        shown = true;
        return mounted;
      };
      return {
        dispose,
        mount,
        // Lets the rest run its course, the virtualizer recomputing its files after every step.
        settle: (at: number): number[] => {
          for (let step = 0; step < 20; step++) {
            vi.advanceTimersByTime(200);
            mount(at);
          }
          return mount(at);
        },
        hide: (): void => {
          shown = false;
        },
        replace: (next: (files: string[]) => string[]): void => {
          files = next(files);
        },
        resize: (height: number): void => {
          screen = height;
        },
        scrolled: window.scrolled,
        correcting: window.correcting,
        changed: window.changed,
      };
    });
  }

  it("mounts one more file on each side per step of rest", () => {
    const page = review();
    expect(page.mount(5)).toEqual([4, 5, 6]);
    vi.advanceTimersByTime(199);
    expect(page.mount(5)).toEqual([4, 5, 6]);
    vi.advanceTimersByTime(1);
    expect(page.mount(5)).toEqual(span(3, 7));
    vi.advanceTimersByTime(50);
    expect(page.mount(5)).toEqual(span(2, 8));
    vi.advanceTimersByTime(50);
    expect(page.mount(5)).toEqual(span(2, 8));
    expect(vi.getTimerCount()).toBe(0);
    page.dispose();
  });

  it("mounts as many short files as fill the screens ahead, up to eight a side", () => {
    const short = review(() => 400);
    expect(short.settle(10)).toEqual(span(4, 16));
    short.dispose();
    const tiny = review(() => 100);
    expect(tiny.settle(10)).toEqual(span(1, 19));
    tiny.dispose();
  });

  it("reaches each side by its own files' heights, at least two files however tall", () => {
    const page = review((index) => (index < 10 ? 250 : 3000));
    expect(page.settle(10)).toEqual(span(1, 13));
    page.dispose();
  });

  it("keeps two files a side before the screen has a height", () => {
    const page = review();
    page.resize(0);
    expect(page.settle(5)).toEqual(span(2, 8));
    page.dispose();
  });

  it("moves each side to a reach that changed after the rest settled, a file per step", () => {
    let below = 1000;
    let above = 1000;
    const page = review((index) => (index < 10 ? above : below));
    expect(page.settle(10)).toEqual(span(7, 13));
    expect(vi.getTimerCount()).toBe(0);
    // Shorter files below: only that side reaches further.
    below = 250;
    page.changed();
    vi.advanceTimersByTime(50);
    expect(page.mount(10)).toEqual(span(7, 14));
    expect(page.settle(10)).toEqual(span(7, 19));
    // A taller screen above files that grew past it: the side above lets files go.
    above = 4000;
    page.changed();
    expect(page.settle(10)).toEqual(span(7, 19));
    page.dispose();
  });

  it("steps on when the viewport grows, with no range change to report it", () => {
    const page = review(() => 400);
    page.resize(300);
    expect(page.settle(5)).toEqual(span(2, 8));
    page.resize(1000);
    page.changed();
    expect(page.settle(5)).toEqual(span(0, 11));
    page.dispose();
  });

  it("steps on when files join a settled list", () => {
    const page = review(() => 400, 4);
    expect(page.settle(0)).toEqual(span(0, 3));
    page.replace((files) => [...files, "file4", "file5", "file6", "file7"]);
    page.mount(0);
    expect(page.settle(0)).toEqual(span(0, 6));
    page.dispose();
  });

  it("unmounts nothing through a scroll, then lets go of a file per step of rest", () => {
    const page = review();
    expect(page.settle(5)).toEqual(span(2, 8));
    for (let at = 6; at <= 12; at++) {
      page.scrolled(at * 100);
      expect(page.mount(at)).toEqual(span(2, at + 1 > 8 ? at + 1 : 8));
    }
    vi.advanceTimersByTime(200);
    expect(page.mount(12)).toEqual(span(3, 14));
    vi.advanceTimersByTime(50);
    expect(page.mount(12)).toEqual(span(4, 15));
    expect(page.settle(12)).toEqual(span(9, 15));
    expect(vi.getTimerCount()).toBe(0);
    page.dispose();
  });

  it("keeps resting through a scroll that only corrects for a file sized above", () => {
    const page = review();
    page.scrolled(500);
    page.mount(5);
    vi.advanceTimersByTime(200);
    expect(page.mount(5)).toEqual(span(3, 7));
    // Whatever position the correction lands on, clamped or not.
    page.correcting(() => page.scrolled(0));
    vi.advanceTimersByTime(50);
    expect(page.mount(5)).toEqual(span(2, 8));
    page.scrolled(600);
    expect(page.mount(5)).toEqual(span(2, 8));
    vi.advanceTimersByTime(199);
    expect(page.mount(5)).toEqual(span(2, 8));
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
    expect(page.mount(5)).toEqual(span(3, 7));
    page.dispose();
  });

  it("mounts only the overscan after a jump from rest, then rests up again", async () => {
    const page = review();
    page.settle(5);
    expect(page.mount(15)).toEqual([14, 15, 16]);
    await Promise.resolve();
    expect(page.mount(15)).toEqual([14, 15, 16]);
    expect(page.settle(15)).toEqual(span(12, 18));
    page.dispose();
  });

  it("mounts only the overscan when a rested page comes back with nothing rendered", async () => {
    const page = review();
    page.settle(5);
    page.hide();
    expect(page.mount(5)).toEqual([4, 5, 6]);
    await Promise.resolve();
    expect(page.mount(5)).toEqual([4, 5, 6]);
    vi.advanceTimersByTime(200);
    expect(page.mount(5)).toEqual(span(3, 7));
    page.dispose();
  });

  it("mounts only the overscan of a replaced list once, then rests on it", async () => {
    const page = review();
    page.settle(5);
    page.replace((files) => files.map((file) => `other/${file}`));
    expect(page.mount(5)).toEqual([4, 5, 6]);
    await Promise.resolve();
    expect(page.settle(5)).toEqual(span(2, 8));
    page.dispose();
  });

  it("carries the mounted files over when the list shifts them to other indexes", () => {
    const page = review();
    page.settle(5);
    page.replace((files) => files.slice(1));
    page.scrolled(100);
    expect(page.mount(4)).toEqual(span(1, 7));
    page.dispose();
  });

  it("does not rest again once disposed", async () => {
    const page = review();
    page.settle(5);
    page.hide();
    page.mount(5);
    page.dispose();
    await Promise.resolve();
    expect(vi.getTimerCount()).toBe(0);
  });

  it("follows the setting live, and lets everything past the overscan go at 0", () => {
    const page = review();
    expect(page.settle(5)).toHaveLength(7);
    options.changed({ reviewScreensAhead: 0 });
    expect(page.mount(5)).toEqual(span(2, 8));
    expect(page.settle(5)).toEqual([4, 5, 6]);
    page.dispose();
  });
});
