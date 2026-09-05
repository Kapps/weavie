import { createSignal } from "solid-js";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  createListNavigation,
  type ListNavigation,
  type ListNavigationOptions,
  nextIndex,
} from "./list-navigation";

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

/**
 * The rows the primitive scrolls to live in the document; this stands in for them under the node test env.
 * Each row registers under the very address `row()` hands the DOM, so a reveal that doesn't address its own
 * rows finds nothing and the assertion fails. Reveals arrive in `revealed` as "<row> <block>".
 */
const stubRows = (nav: ListNavigation, count: number): string[] => {
  const revealed: string[] = [];
  const rows = new Map<string, number>();
  for (let row = 0; row < count; row++) {
    rows.set(nav.row(row)["data-list-row"], row);
  }
  vi.stubGlobal("document", {
    querySelector: (selector: string) => {
      const row = rows.get(/^\[data-list-row="(.*)"]$/.exec(selector)?.[1] ?? "");
      return row === undefined
        ? null
        : {
            scrollIntoView: (options: ScrollIntoViewOptions) =>
              revealed.push(`${row} ${options.block}`),
          };
    },
  });
  return revealed;
};

const navigation = (
  count: number,
  overrides: Partial<ListNavigationOptions>,
): ListNavigation & { accepted: number[]; dismissals: () => number; revealed: string[] } => {
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
  return { ...nav, accepted, dismissals: () => dismissals, revealed: stubRows(nav, count) };
};

afterEach(() => vi.unstubAllGlobals());

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
  it("moves the highlight and swallows the arrow keys", () => {
    const nav = navigation(3, {});
    const down = pressed("ArrowDown");
    expect(nav.onKeyDown(down.event)).toBe(true);
    expect(nav.index()).toBe(1);
    expect(down.prevented()).toBe(true);
    expect(down.stopped()).toBe(true);
    expect(nav.onKeyDown(pressed("ArrowUp").event)).toBe(true);
    expect(nav.index()).toBe(0);
  });

  it("swallows every key it owns, so nothing behind the list acts on it too", () => {
    const nav = navigation(3, {});
    for (const key of ["ArrowDown", "ArrowUp", "Enter", "Escape"]) {
      const press = pressed(key);
      expect(nav.onKeyDown(press.event)).toBe(true);
      expect(press.prevented()).toBe(true);
      expect(press.stopped()).toBe(true);
    }
    const other = pressed("a");
    expect(nav.onKeyDown(other.event)).toBe(false);
    expect(other.prevented()).toBe(false);
    expect(other.stopped()).toBe(false);
  });

  it("swallows the arrow keys on an empty list, so the caret behind it stays put", () => {
    const nav = navigation(0, {});
    const down = pressed("ArrowDown");
    expect(nav.onKeyDown(down.event)).toBe(true);
    expect(down.prevented()).toBe(true);
    expect(down.stopped()).toBe(true);
  });

  it("reports -1 from an empty list, so accept acts on what was typed instead of a row", () => {
    const nav = navigation(0, {});
    expect(nav.index()).toBe(-1);
    nav.onKeyDown(pressed("Enter").event);
    expect(nav.accepted).toEqual([-1]);
  });

  it("starts with nothing highlighted when asked to, until an arrow picks a row", () => {
    const nav = navigation(3, { initialIndex: -1 });
    expect(nav.index()).toBe(-1);
    nav.onKeyDown(pressed("Enter").event);
    nav.onKeyDown(pressed("ArrowDown").event);
    nav.onKeyDown(pressed("Enter").event);
    expect(nav.accepted).toEqual([-1, 0]);
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
    });
    stubRows(nav, 5);
    nav.setIndex(4);
    setCount(2);
    expect(nav.index()).toBe(1);
    // The move starts from the clamped row, not the dangling one.
    nav.onKeyDown(pressed("ArrowDown").event);
    expect(nav.index()).toBe(0);
  });

  it("leaves the highlight on the first row of a list that arrives after the arrow key", () => {
    const [count, setCount] = createSignal(0);
    const nav = createListNavigation({
      count,
      edges: "wrap",
      initialIndex: 0,
      acceptKeys: ["Enter"],
      onAccept: () => {},
      onDismiss: () => {},
    });
    stubRows(nav, 3);
    nav.onKeyDown(pressed("ArrowDown").event);
    setCount(3);
    expect(nav.index()).toBe(0);
  });

  it("scrolls the row an arrow moved to into view", async () => {
    const nav = navigation(3, {});
    nav.onKeyDown(pressed("ArrowDown").event);
    await Promise.resolve();
    expect(nav.revealed).toEqual(["1 nearest"]);
  });

  it("scrolls to the row the list settled on, not the one it left", async () => {
    const nav = navigation(3, {});
    nav.onKeyDown(pressed("ArrowUp").event);
    nav.setIndex(2);
    await Promise.resolve();
    expect(nav.revealed).toEqual(["2 nearest"]);
  });

  it("reveals on demand for a highlight that no key moved", async () => {
    const nav = navigation(3, {});
    nav.setIndex(2);
    nav.reveal("center");
    await Promise.resolve();
    expect(nav.revealed).toEqual(["2 center"]);
  });

  it("addresses each list's rows separately, so one list never scrolls another's row", () => {
    const first = navigation(3, {});
    const second = navigation(3, {});
    expect(first.row(1)["data-list-row"]).not.toBe(second.row(1)["data-list-row"]);
  });

  it("moves the highlight to a hovered row", () => {
    const nav = navigation(3, {});
    nav.row(2).onMouseMove();
    expect(nav.index()).toBe(2);
  });
});
