import { afterEach, describe, expect, it, vi } from "vitest";
import { ReviewScrollState } from "./review-scroll-state";

function fixture(duration: number) {
  const frames = new Set<() => void>();
  const state = new ReviewScrollState({
    forceIntegerValues: false,
    smoothScrollDuration: duration,
    scheduleAtNextAnimationFrame: (callback) => {
      frames.add(callback);
      return { dispose: () => frames.delete(callback) };
    },
  });
  state.setScrollDimensions(
    { width: 100, scrollWidth: 500, height: 100, scrollHeight: 1_000 },
    false,
  );
  const commits: number[] = [];
  state.onScroll((event) => commits.push(event.scrollTop));
  const wheel = (delta: number): void => {
    state.wheel(() => {
      state.setScrollPositionNow({ scrollTop: state.getFutureScrollPosition().scrollTop + delta });
    });
  };
  const frame = (): void => {
    const callbacks = [...frames];
    frames.clear();
    for (const callback of callbacks) callback();
  };
  return { state, frames, commits, wheel, frame };
}

afterEach(() => vi.useRealTimers());

describe("review wheel scroll state", () => {
  it("keeps geometry committed while accumulating a fractional burst and reversal", () => {
    const f = fixture(0);
    f.wheel(30.5);
    f.wheel(20.25);
    f.wheel(-10);
    expect(f.state.getFutureScrollPosition().scrollTop).toBe(40.75);
    expect(f.state.getCurrentScrollPosition().scrollTop).toBe(0);
    expect(f.commits).toEqual([]);
    expect(f.frames.size).toBe(1);
    f.frame();
    expect(f.commits).toEqual([40.75]);
    expect(f.frames.size).toBe(0);
    f.state.dispose();
  });

  it("clamps every event before accumulating the next delta", () => {
    const f = fixture(0);
    f.wheel(1_000);
    f.wheel(-100);
    f.frame();
    expect(f.commits).toEqual([800]);
    f.wheel(-1_000);
    f.wheel(25);
    f.frame();
    expect(f.commits).toEqual([800, 25]);
    f.state.dispose();
  });

  it("retains the other queued axis on partial writes", () => {
    const f = fixture(0);
    f.wheel(50);
    f.state.wheel(() => f.state.setScrollPositionNow({ scrollLeft: 25 }));
    f.frame();
    expect(f.state.getCurrentScrollPosition()).toMatchObject({ scrollTop: 50, scrollLeft: 25 });
    f.state.dispose();
  });

  it("absolute navigation supersedes queued input immediately", () => {
    const f = fixture(0);
    f.wheel(100);
    f.state.setScrollPositionNow({ scrollTop: 600 });
    expect(f.commits).toEqual([600]);
    expect(f.frames.size).toBe(0);
    f.frame();
    expect(f.state.getFutureScrollPosition().scrollTop).toBe(600);
    f.state.dispose();
  });

  it("moves both committed and queued positions with a geometry anchor", () => {
    const f = fixture(0);
    f.state.setScrollPositionNow({ scrollTop: 300 });
    f.wheel(100);
    f.state.setScrollAnchor(350, 50);
    expect(f.state.getCurrentScrollPosition().scrollTop).toBe(350);
    expect(f.state.getFutureScrollPosition().scrollTop).toBe(450);
    f.frame();
    expect(f.commits).toEqual([300, 350, 450]);
    f.state.dispose();
  });

  it("does not double-apply an anchor when dimension shrink already clamps the position", () => {
    const f = fixture(0);
    f.state.setScrollPositionNow({ scrollTop: 900 });
    f.wheel(-50);
    f.state.setScrollDimensions({ scrollHeight: 900 }, false);
    f.state.setScrollAnchor(800, -100);
    expect(f.state.getCurrentScrollPosition().scrollTop).toBe(800);
    expect(f.state.getFutureScrollPosition().scrollTop).toBe(750);
    f.frame();
    expect(f.state.getCurrentScrollPosition().scrollTop).toBe(750);
    f.state.dispose();
  });

  it("does not publish a redundant update when cancelling smooth motion from a raw clamp", () => {
    const f = fixture(125);
    f.state.setScrollPositionNow({ scrollTop: 1_000 });
    f.commits.length = 0;
    f.state.setScrollPositionSmooth({ scrollTop: 800 });
    f.wheel(-50);
    expect(f.commits).toEqual([]);
    f.frame();
    expect(f.commits).toEqual([750]);
    f.state.dispose();
  });

  it("validates queued input against changed dimensions before the next event or commit", () => {
    const f = fixture(0);
    f.wheel(850);
    f.state.setScrollDimensions({ scrollHeight: 600 }, false);
    expect(f.state.getFutureScrollPosition().scrollTop).toBe(500);
    f.wheel(-50);
    f.frame();
    expect(f.state.getCurrentScrollPosition().scrollTop).toBe(450);
    f.state.dispose();
  });

  it("cancels an old smooth animation when immediate wheel input takes over", () => {
    vi.useFakeTimers();
    const f = fixture(125);
    f.state.setScrollPositionSmooth({ scrollTop: 400 });
    expect(f.state.hasPendingScrollAnimation()).toBe(true);
    f.wheel(50);
    expect(f.state.hasPendingScrollAnimation()).toBe(false);
    expect(f.commits).toEqual([]);
    expect(f.frames.size).toBe(1);
    f.frame();
    expect(f.commits).toEqual([450]);
    f.state.dispose();
  });

  it("hands the accumulated destination to Monaco smooth scrolling", () => {
    vi.useFakeTimers();
    const f = fixture(125);
    f.wheel(100);
    f.state.wheel(() =>
      f.state.setScrollPositionSmooth({
        scrollTop: f.state.getFutureScrollPosition().scrollTop + 50,
      }),
    );
    expect(f.state.getFutureScrollPosition().scrollTop).toBe(150);
    expect(f.frames.size).toBe(1);
    vi.advanceTimersByTime(16);
    f.frame();
    expect(f.commits[0]).toBeGreaterThan(0);
    expect(f.commits[0]).toBeLessThan(150);
    vi.advanceTimersByTime(150);
    f.frame();
    expect(f.state.getCurrentScrollPosition().scrollTop).toBe(150);
    f.state.dispose();
  });

  it("coalesces physical wheel input too when smooth scrolling is disabled", () => {
    const f = fixture(0);
    f.state.wheel(() => f.state.setScrollPositionSmooth({ scrollTop: 100 }));
    f.state.wheel(() => f.state.setScrollPositionSmooth({ scrollTop: 150 }));
    expect(f.commits).toEqual([]);
    expect(f.frames.size).toBe(1);
    f.frame();
    expect(f.commits).toEqual([150]);
    f.state.dispose();
  });

  it("does not overwrite reentrant navigation during an anchor or wheel commit", () => {
    const f = fixture(0);
    const subscription = f.state.onScroll(() => {
      subscription.dispose();
      f.state.setScrollPositionNow({ scrollTop: 700 });
    });
    f.wheel(100);
    f.state.setScrollAnchor(50, 50);
    expect(f.frames.size).toBe(0);
    f.frame();
    expect(f.state.getCurrentScrollPosition().scrollTop).toBe(700);
    const next = f.state.onScroll(() => {
      next.dispose();
      f.wheel(20);
    });
    f.wheel(10);
    f.frame();
    expect(f.frames.size).toBe(1);
    f.frame();
    expect(f.state.getCurrentScrollPosition().scrollTop).toBe(730);
    f.state.dispose();
  });

  it("cancels pending callbacks on disposal", () => {
    const f = fixture(0);
    f.wheel(100);
    f.state.dispose();
    f.frame();
    expect(f.frames.size).toBe(0);
    expect(f.commits).toEqual([]);
  });
});
