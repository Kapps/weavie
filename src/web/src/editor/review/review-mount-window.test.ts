import type { Range } from "@tanstack/solid-virtual";
import { createRoot } from "solid-js";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const options = vi.hoisted(() => ({
  reviewFilesAhead: 2,
  changed: (_options: { reviewFilesAhead: number }): void => {},
}));
vi.mock("../../editor-options", () => ({
  currentEditorOptions: () => options,
  onEditorOptionsChanged: (handler: typeof options.changed) => {
    options.changed = handler;
    return () => {};
  },
}));

import { createMountWindow, mountedSpan, type Span } from "./review-mount-window";

const visible = (startIndex: number, endIndex: number): Range => ({
  startIndex,
  endIndex,
  overscan: 1,
  count: 20,
});

describe("mountedSpan", () => {
  it("reaches one file further on each side per rested step, within the file list", () => {
    expect(mountedSpan(visible(5, 5), 1, 2, [4, 6]).span).toEqual([3, 7]);
    expect(mountedSpan(visible(0, 0), 2, 2, [0, 1]).span).toEqual([0, 3]);
    expect(mountedSpan(visible(19, 19), 2, 2, [18, 19]).span).toEqual([16, 19]);
  });

  it("keeps rested files mounted through a scroll into them", () => {
    expect(mountedSpan(visible(6, 6), 0, 2, [2, 8]).span).toEqual([2, 8]);
    expect(mountedSpan(visible(7, 7), 0, 2, [2, 8]).span).toEqual([2, 8]);
  });

  it("unmounts a file only once it is two past the reach, or the review rests again", () => {
    expect(mountedSpan(visible(8, 8), 0, 2, [2, 8]).span).toEqual([3, 9]);
    expect(mountedSpan(visible(7, 7), 2, 2, [2, 8]).span).toEqual([4, 10]);
  });

  it("mounts only the overscan when nothing mounted carries over, however long the review rested", () => {
    expect(mountedSpan(visible(5, 5), 2, 2, undefined)).toEqual({ span: [4, 6], fresh: true });
    expect(mountedSpan(visible(9, 9), 2, 2, [2, 5])).toEqual({ span: [8, 10], fresh: true });
    expect(mountedSpan(visible(5, 5), 2, 2, [4, 6]).fresh).toBe(false);
  });

  it("is the plain overscan with no reach", () => {
    expect(mountedSpan(visible(6, 6), 0, 0, [4, 6]).span).toEqual([5, 7]);
  });
});

describe("createMountWindow", () => {
  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    vi.useRealTimers();
    options.reviewFilesAhead = 2;
  });

  // A review showing file `at`, whose page holds whatever the last extraction mounted.
  function review() {
    let rendered: Span | undefined;
    return createRoot((dispose) => {
      const window = createMountWindow(() => rendered);
      const mount = (at: number): number[] => {
        const files = window.extractor()(visible(at, at));
        rendered = [files[0]!, files.at(-1)!];
        return files;
      };
      const hide = (): void => {
        rendered = undefined;
      };
      return { dispose, mount, hide, scrolled: window.scrolled };
    });
  }

  it("mounts one more file on each side per step of rest", () => {
    const page = review();
    expect(page.mount(5)).toEqual([4, 5, 6]);
    vi.advanceTimersByTime(199);
    expect(page.mount(5)).toEqual([4, 5, 6]);
    vi.advanceTimersByTime(1);
    expect(page.mount(5)).toEqual([3, 4, 5, 6, 7]);
    vi.advanceTimersByTime(150);
    expect(page.mount(5)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    page.dispose();
  });

  it("holds the rested files through a scroll and mounts nothing new until it rests again", () => {
    const page = review();
    page.mount(5);
    vi.advanceTimersByTime(350);
    page.mount(5);
    page.scrolled(100);
    expect(page.mount(6)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    page.scrolled(200);
    expect(page.mount(7)).toEqual([2, 3, 4, 5, 6, 7, 8]);
    vi.advanceTimersByTime(350);
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
    vi.advanceTimersByTime(350);
    page.mount(5);
    expect(page.mount(15)).toEqual([14, 15, 16]);
    await Promise.resolve();
    expect(page.mount(15)).toEqual([14, 15, 16]);
    vi.advanceTimersByTime(350);
    expect(page.mount(15)).toEqual([12, 13, 14, 15, 16, 17, 18]);
    page.dispose();
  });

  it("mounts only the overscan when a rested page comes back with nothing rendered", async () => {
    const page = review();
    page.mount(5);
    vi.advanceTimersByTime(350);
    page.mount(5);
    page.hide();
    expect(page.mount(5)).toEqual([4, 5, 6]);
    await Promise.resolve();
    vi.advanceTimersByTime(200);
    expect(page.mount(5)).toEqual([3, 4, 5, 6, 7]);
    page.dispose();
  });

  it("follows the setting live, and mounts nothing ahead at 0", () => {
    const page = review();
    page.mount(5);
    vi.advanceTimersByTime(350);
    expect(page.mount(5)).toHaveLength(7);
    options.changed({ reviewFilesAhead: 0 });
    expect(page.mount(5)).toEqual([4, 5, 6]);
    vi.advanceTimersByTime(1000);
    expect(page.mount(5)).toEqual([4, 5, 6]);
    page.dispose();
  });
});
