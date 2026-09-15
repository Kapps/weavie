import {
  type INewScrollDimensions,
  type INewScrollPosition,
  Scrollable,
  type ScrollEvent,
} from "@codingame/monaco-vscode-api/vscode/vs/base/common/scrollable";
import { afterEach, beforeEach, expect, it, vi } from "vitest";

const dimensions = { width: 400, scrollWidth: 2000, height: 400, scrollHeight: 2000 };
const zero = { scrollLeft: 0, scrollTop: 0 };
const drivers: Array<{ model: Scrollable; frames: Set<() => void> }> = [];
let now = 1000;

function driver(rect: INewScrollDimensions, position: INewScrollPosition, integer: boolean) {
  const frames = new Set<() => void>();
  const model = new Scrollable({
    forceIntegerValues: integer,
    smoothScrollDuration: 125,
    scheduleAtNextAnimationFrame(callback) {
      frames.add(callback);
      return { dispose: () => frames.delete(callback) };
    },
  });
  model.setScrollDimensions(rect, false);
  model.setScrollPositionNow(position);
  const result = { model, frames };
  drivers.push(result);
  return result;
}

function step(elapsed: number) {
  now = 1000 + elapsed;
  const callbacks = drivers.flatMap(({ frames }) => {
    const pending = [...frames];
    frames.clear();
    return pending;
  });
  for (const callback of callbacks) callback();
}

const top = (model: Scrollable): number => model.getCurrentScrollPosition().scrollTop;

beforeEach(() => {
  now = 1000;
  vi.spyOn(Date, "now").mockImplementation(() => now);
});

afterEach(() => {
  for (const { model, frames } of drivers) {
    model.dispose();
    expect(frames.size).toBe(0);
  }
  drivers.length = 0;
  vi.restoreAllMocks();
});

it("anchors an atomic shrink to the visible position without reviving overscroll", () => {
  const { model } = driver(
    { ...dimensions, height: 100, scrollHeight: 1000 },
    { scrollTop: 1200 },
    false,
  );
  expect(top(model)).toBe(900);
  model.setScrollGeometry({ scrollHeight: 500 }, { scrollLeft: 0, scrollTop: -500 });
  expect(top(model)).toBe(400);
  model.setScrollGeometry({ scrollHeight: 2000 }, zero);
  expect(top(model)).toBe(400);
});

it("preserves fractional geometry changes on both axes", () => {
  const { model } = driver(dimensions, { scrollLeft: 300.125, scrollTop: 300.125 }, false);
  model.setScrollGeometry({}, { scrollLeft: -0.25, scrollTop: 0.375 });
  expect(model.getCurrentScrollPosition()).toMatchObject({ scrollLeft: 299.875, scrollTop: 300.5 });
});

it("translates unfinished motion without changing its curve, deadline or scheduled frame count", () => {
  const control = driver(dimensions, { scrollLeft: 300, scrollTop: 300 }, false);
  const probe = driver(dimensions, { scrollLeft: 300, scrollTop: 300 }, false);
  for (const { model } of [control, probe])
    model.setScrollPositionSmooth({ scrollLeft: 600, scrollTop: 600 }, false);
  step(30);
  probe.model.setScrollGeometry(
    { scrollHeight: 2080, scrollWidth: 2080 },
    { scrollLeft: 80, scrollTop: 80 },
  );
  for (const time of [30, 40, 60]) {
    step(time);
    expect(top(probe.model)).toBeCloseTo(top(control.model) + 80, 8);
    expect(probe.model.getCurrentScrollPosition().scrollLeft).toBeCloseTo(
      control.model.getCurrentScrollPosition().scrollLeft + 80,
      8,
    );
    expect(probe.frames.size).toBe(1);
  }
  probe.model.setScrollGeometry(
    { scrollHeight: 1980, scrollWidth: 1980 },
    { scrollLeft: -100, scrollTop: -100 },
  );
  for (const time of [60, 90, 120, 125]) {
    step(time);
    expect(top(probe.model)).toBeCloseTo(top(control.model) - 20, 8);
    expect(probe.model.hasPendingScrollAnimation()).toBe(control.model.hasPendingScrollAnimation());
  }
  expect(probe.frames.size).toBe(0);
});

it("clips motion at new bounds without changing the curve before the boundary", () => {
  const control = driver(dimensions, { scrollTop: 100 }, false);
  const probe = driver(dimensions, { scrollTop: 100 }, false);
  for (const { model } of [control, probe])
    model.setScrollPositionSmooth({ scrollTop: 700 }, false);
  step(10);
  const before = top(probe.model);
  probe.model.setScrollGeometry({ scrollHeight: 850 }, zero);
  expect(top(probe.model)).toBe(before);
  expect(probe.model.getFutureScrollPosition().scrollTop).toBe(450);
  for (const time of [10, 20, 30, 70, 125]) {
    step(time);
    expect(top(probe.model)).toBeCloseTo(Math.min(450, top(control.model)), 8);
  }
});

it("does not reveal clipped motion after another geometry adjustment", () => {
  const { model } = driver(dimensions, { scrollTop: 100 }, false);
  model.setScrollPositionSmooth({ scrollTop: 700 }, false);
  step(10);
  model.setScrollGeometry({ scrollHeight: 850 }, zero);
  step(70);
  expect(top(model)).toBe(450);
  model.setScrollGeometry({}, { scrollLeft: 0, scrollTop: -300 });
  expect(top(model)).toBe(150);
  step(70);
  expect(top(model)).toBe(150);
  step(125);
  expect(top(model)).toBe(150);
});

it.each([false, true])("keeps an explicit clipped target after expansion (reuse=%s)", (reuse) => {
  const { model } = driver(dimensions, { scrollTop: 100 }, false);
  model.setScrollPositionSmooth({ scrollTop: 700 }, false);
  step(10);
  model.setScrollGeometry({ scrollHeight: 850 }, zero);
  model.setScrollPositionSmooth({ scrollTop: 450 }, reuse);
  model.setScrollGeometry({ scrollHeight: 2000 }, zero);
  expect(model.getFutureScrollPosition().scrollTop).toBe(450);
  step(125);
  expect(top(model)).toBe(450);
});

it("composes successive geometry changes like sequential translations and clamps", () => {
  const control = driver(dimensions, { scrollTop: 100 }, false);
  const probe = driver(dimensions, { scrollTop: 100 }, false);
  for (const { model } of [control, probe])
    model.setScrollPositionSmooth({ scrollTop: 1500 }, false);
  const changes: Array<{ delta: number; maximum: number }> = [];
  let seed = 17;
  const random = () => {
    seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
    return seed / 2 ** 32;
  };
  const expected = () =>
    changes.reduce(
      (position, change) => Math.max(0, Math.min(change.maximum, position + change.delta)),
      top(control.model),
    );
  for (let time = 0; time <= 125; time += 5) {
    step(time);
    expect(top(probe.model)).toBeCloseTo(expected(), 8);
    const delta = random() * 500 - 250;
    const height = 100 + random() * 600;
    const maximum = random() * 1200;
    changes.push({ delta, maximum });
    probe.model.setScrollGeometry(
      { height, scrollHeight: height + maximum },
      { scrollLeft: 0, scrollTop: delta },
    );
    expect(top(probe.model)).toBeCloseTo(expected(), 8);
    step(time);
    expect(top(probe.model)).toBeCloseTo(expected(), 8);
  }
});

it("preserves the long-distance curve when viewport dimensions change", () => {
  const control = driver({ ...dimensions, height: 100 }, { scrollTop: 300 }, false);
  const probe = driver({ ...dimensions, height: 100 }, { scrollTop: 300 }, false);
  for (const { model } of [control, probe])
    model.setScrollPositionSmooth({ scrollTop: 1000 }, false);
  step(20);
  probe.model.setScrollGeometry({ height: 800 }, { scrollLeft: 0, scrollTop: -350 });
  for (const time of [20, 40, 70, 110, 125]) {
    step(time);
    expect(top(probe.model)).toBeCloseTo(Math.max(0, top(control.model) - 350), 8);
  }
});

it("uses the same integer translation for current position and unfinished motion", () => {
  const { model } = driver(dimensions, { scrollTop: 100 }, true);
  model.setScrollPositionSmooth({ scrollTop: 700 }, false);
  step(10);
  expect(top(model)).toBe(329);
  for (const delta of [-0.1, 0.1, -1.75, 1.75, 0.375]) {
    model.setScrollGeometry({}, { scrollLeft: 0, scrollTop: delta });
    const before = top(model);
    step(10);
    expect(top(model)).toBe(before);
  }
});

it("publishes geometry and the corrected future target atomically", () => {
  const { model } = driver(dimensions, { scrollTop: 300 }, false);
  model.setScrollPositionSmooth({ scrollTop: 600 }, false);
  step(30);
  const observed: Array<{ height: number; future: number; top: number }> = [];
  model.onScroll((event) =>
    observed.push({
      height: model.getScrollDimensions().scrollHeight,
      future: model.getFutureScrollPosition().scrollTop,
      top: event.scrollTop,
    }),
  );
  model.setScrollGeometry({ scrollHeight: 2080 }, { scrollLeft: 0, scrollTop: 80 });
  expect(observed).toEqual([{ height: 2080, future: 680, top: top(model) }]);
});

it.each(["cancel", "replace"])("preserves reentrant %s during the final frame", (action) => {
  const { model, frames } = driver(dimensions, { scrollTop: 300 }, false);
  let triggered = false;
  model.onScroll((event) => {
    if (triggered || event.source !== "position" || event.scrollTop !== 600) return;
    triggered = true;
    if (action === "cancel") model.setScrollPositionNow({ scrollTop: 800 });
    else model.setScrollPositionSmooth({ scrollTop: 800 }, false);
  });
  model.setScrollPositionSmooth({ scrollTop: 600 }, false);
  step(125);
  expect(triggered).toBe(true);
  expect(frames.size).toBe(action === "replace" ? 1 : 0);
  if (action === "replace") step(250);
  expect(top(model)).toBe(800);
  expect(frames.size).toBe(0);
});

it("publishes idle completion even when geometry has already clipped the final position", () => {
  const { model } = driver(dimensions, { scrollTop: 300 }, false);
  const events: ScrollEvent[] = [];
  model.onScroll((event) => events.push(event));
  model.setScrollPositionSmooth({ scrollTop: 600 }, false);
  expect(events.at(-1)).toMatchObject({ source: "position", inSmoothScrolling: true });
  step(30);
  model.setScrollGeometry({ scrollHeight: 400 }, zero);
  expect(top(model)).toBe(0);
  step(125);
  expect(events.at(-1)).toMatchObject({
    source: "position",
    inSmoothScrolling: false,
    scrollTop: 0,
  });
  expect(events.some((event) => event.source === "geometry")).toBe(true);
  expect(model.hasPendingScrollAnimation()).toBe(false);
});

it("reports boundary input while preserving equality for ordinary position synchronization", () => {
  const { model } = driver(dimensions, { scrollTop: 500 }, false);
  model.setScrollGeometry({ scrollHeight: 780 }, zero);
  const events: ScrollEvent[] = [];
  model.onScroll((event) => events.push(event));
  model.setScrollPositionNow({ scrollTop: 500 });
  expect(events).toEqual([]);
  model.setScrollPositionFromInput({ scrollTop: 500 }, false);
  expect(events).toHaveLength(1);
  expect(events[0]).toMatchObject({ source: "position", inSmoothScrolling: false, scrollTop: 380 });
  model.setScrollPositionNow({ scrollTop: 500 });
  expect(events).toHaveLength(1);
  model.setScrollPositionFromInput({ scrollTop: 300 }, false);
  expect(events).toHaveLength(2);
  expect(top(model)).toBe(300);
});

it("reports repeated input toward an active target without replacing its animation", () => {
  const { model, frames } = driver(dimensions, { scrollTop: 300 }, false);
  const events: ScrollEvent[] = [];
  model.onScroll((event) => events.push(event));
  model.setScrollPositionSmooth({ scrollTop: 600 }, false);
  const scheduled = [...frames];
  model.setScrollPositionFromInput({ scrollTop: 600 }, true);
  expect([...frames]).toEqual(scheduled);
  expect(events).toHaveLength(2);
  expect(events.at(-1)).toMatchObject({ source: "position", inSmoothScrolling: true });
  step(125);
  expect(top(model)).toBe(600);
  expect(model.hasPendingScrollAnimation()).toBe(false);
});
