import { Scrollable } from "@codingame/monaco-vscode-api/vscode/vs/base/common/scrollable";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The review scroller (review-scroll.ts) relies on two patched behaviours of Monaco's Scrollable.
describe("review wheel glide", () => {
  let frames: (() => void)[];
  let scrollable: Scrollable;
  const top = (): number => scrollable.getCurrentScrollPosition().scrollTop;
  const frame = (): number => {
    vi.advanceTimersByTime(8);
    for (const callback of frames.splice(0)) callback();
    return top();
  };
  const glideBy = (delta: number): void =>
    scrollable.setScrollPositionSmooth(
      { scrollTop: scrollable.getFutureScrollPosition().scrollTop + delta },
      false,
    );

  beforeEach(() => {
    vi.useFakeTimers();
    frames = [];
    scrollable = new Scrollable({
      forceIntegerValues: false,
      smoothScrollDuration: 125,
      scheduleAtNextAnimationFrame: (callback) => {
        frames.push(callback);
        return { dispose: () => frames.splice(frames.indexOf(callback), 1) };
      },
    });
    scrollable.setScrollDimensions({ width: 100, height: 500, scrollHeight: 100_000 }, true);
    scrollable.setScrollPositionNow({ scrollTop: 1000 });
  });
  afterEach(() => {
    scrollable.dispose();
    vi.useRealTimers();
  });

  it("carries a glide through a size correction above the viewport", () => {
    glideBy(120);
    const before = frame();
    scrollable.shiftScrollTop(300);
    expect(top()).toBe(before + 300);
    expect(scrollable.hasPendingScrollAnimation()).toBe(true);
    for (let i = 0; i < 40; i++) frame();
    expect(top()).toBe(1420);
  });

  it("continues a glide at its current speed when another notch lands", () => {
    glideBy(120);
    for (let i = 0; i < 4; i++) frame();
    const from = top();
    const speed = frame() - from;
    glideBy(120);
    const previous = top();
    // Restarting the ease-out leaps to its full speed, about three times the glide's speed by now; the merged glide
    // only speeds up as far as its 250 ms cap requires.
    expect(frame() - previous).toBeLessThan(speed * 2);
  });
});
