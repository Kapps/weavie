import { createSignal } from "solid-js";
import { describe, expect, it } from "vitest";
import { createListNavigation, type ListNavigationOptions, nextIndex } from "./list-navigation";

const pressed = (
  key: string,
): { event: KeyboardEvent; prevented: () => boolean; stopped: () => boolean } => {
  let prevented = false;
  let stopped = false;
  const event = {
    key,
    shiftKey: false,
    preventDefault: () => {
      prevented = true;
    },
    stopPropagation: () => {
      stopped = true;
    },
  };
  return { event: event as KeyboardEvent, prevented: () => prevented, stopped: () => stopped };
};

const navigation = (
  count: number,
  overrides: Partial<ListNavigationOptions>,
): ReturnType<typeof createListNavigation> & { accepted: number[]; dismissals: () => number } => {
  const accepted: number[] = [];
  let dismissals = 0;
  const nav = createListNavigation({
    count: () => count,
    edges: "wrap",
    initialIndex: 0,
    acceptKeys: ["Enter"],
    onAccept: (index) => accepted.push(index),
    onDismiss: () => {
      dismissals += 1;
    },
    ...overrides,
  });
  return { ...nav, accepted, dismissals: () => dismissals };
};

describe("nextIndex", () => {
  it("wraps around both ends", () => {
    expect(nextIndex(0, 1, 3, "wrap")).toBe(1);
    expect(nextIndex(2, 1, 3, "wrap")).toBe(0);
    expect(nextIndex(0, -1, 3, "wrap")).toBe(2);
    expect(nextIndex(2, -1, 3, "wrap")).toBe(1);
  });

  it("treats -1 (nothing highlighted) as before the first row", () => {
    expect(nextIndex(-1, 1, 3, "wrap")).toBe(0);
    expect(nextIndex(-1, -1, 3, "wrap")).toBe(2);
  });

  it("stays put rather than dividing by an empty list", () => {
    expect(nextIndex(0, 1, 0, "wrap")).toBe(0);
    expect(nextIndex(-1, -1, 0, "wrap")).toBe(-1);
  });

  it("stops at both ends when clamped", () => {
    expect(nextIndex(0, 1, 3, "clamp")).toBe(1);
    expect(nextIndex(2, 1, 3, "clamp")).toBe(2);
    expect(nextIndex(0, -1, 3, "clamp")).toBe(0);
  });
});

describe("createListNavigation", () => {
  it("moves the highlight and consumes the arrow keys", () => {
    const nav = navigation(3, {});
    const down = pressed("ArrowDown");
    expect(nav.onKeyDown(down.event)).toBe(true);
    expect(nav.index()).toBe(1);
    expect(down.prevented()).toBe(true);
    expect(down.stopped()).toBe(false);
    expect(nav.onKeyDown(pressed("ArrowUp").event)).toBe(true);
    expect(nav.index()).toBe(0);
  });

  it("stops propagation only when the list owns the keys outright", () => {
    const nav = navigation(3, { stopPropagation: true });
    const down = pressed("ArrowDown");
    nav.onKeyDown(down.event);
    expect(down.stopped()).toBe(true);
  });

  it("leaves the arrow keys alone when there is nothing to move to", () => {
    const nav = navigation(0, {});
    const down = pressed("ArrowDown");
    expect(nav.onKeyDown(down.event)).toBe(false);
    expect(down.prevented()).toBe(false);
  });

  it("still swallows the arrow keys on an empty list when asked to", () => {
    const nav = navigation(0, { consumeEmptyArrows: true });
    const down = pressed("ArrowDown");
    expect(nav.onKeyDown(down.event)).toBe(true);
    expect(down.prevented()).toBe(true);
  });

  it("accepts the highlighted row on every accept key, and ignores other keys", () => {
    const nav = navigation(3, { acceptKeys: ["Enter", "Tab"] });
    nav.onKeyDown(pressed("ArrowDown").event);
    nav.onKeyDown(pressed("Enter").event);
    nav.onKeyDown(pressed("Tab").event);
    expect(nav.accepted).toEqual([1, 1]);
    const other = pressed("a");
    expect(nav.onKeyDown(other.event)).toBe(false);
    expect(other.prevented()).toBe(false);
  });

  it("hands the event to onAccept so a list can tell its accept keys apart", () => {
    const keys: string[] = [];
    const nav = createListNavigation({
      count: () => 2,
      edges: "wrap",
      initialIndex: 0,
      acceptKeys: ["Enter", "Tab"],
      onAccept: (_index, event) => keys.push(event.key),
      onDismiss: () => {},
    });
    nav.onKeyDown(pressed("Tab").event);
    expect(keys).toEqual(["Tab"]);
  });

  it("dismisses on Escape", () => {
    const nav = navigation(3, {});
    const dismiss = pressed("Escape");
    expect(nav.onKeyDown(dismiss.event)).toBe(true);
    expect(dismiss.prevented()).toBe(true);
    expect(nav.dismissals()).toBe(1);
  });

  it("reports each move to onMove", () => {
    const moves: number[] = [];
    const nav = navigation(3, { onMove: (index) => moves.push(index) });
    nav.onKeyDown(pressed("ArrowDown").event);
    nav.onKeyDown(pressed("ArrowUp").event);
    expect(moves).toEqual([1, 0]);
  });

  it("keeps the reported index inside a list that shrank under it", () => {
    const [count, setCount] = createSignal(5);
    const nav = createListNavigation({
      count,
      edges: "wrap",
      initialIndex: 0,
      acceptKeys: ["Enter"],
      onAccept: () => {},
      onDismiss: () => {},
      clampIndex: true,
    });
    nav.setIndex(4);
    setCount(2);
    expect(nav.index()).toBe(1);
    // The move starts from the clamped row, not the dangling one.
    nav.onKeyDown(pressed("ArrowDown").event);
    expect(nav.index()).toBe(0);
  });
});
