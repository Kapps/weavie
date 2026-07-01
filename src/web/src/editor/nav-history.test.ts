import { beforeEach, describe, expect, it } from "vitest";
import { type NavHistory, type NavLocation, createNavHistory } from "./nav-history";

let nav: NavHistory;
let visited: NavLocation[];

beforeEach(() => {
  visited = [];
  nav = createNavHistory((loc) => {
    visited.push(loc);
  });
});

describe("createNavHistory", () => {
  it("does not step when there is no history", () => {
    expect(nav.back()).toBe(false);
    expect(nav.forward()).toBe(false);
    expect(visited).toEqual([]);
  });

  it("steps back and forward through recorded jumps", () => {
    nav.record({ path: "/a.ts", line: 1 });
    nav.record({ path: "/b.ts", line: 1 });
    nav.record({ path: "/c.ts", line: 1 });

    expect(nav.back()).toBe(true);
    expect(visited.at(-1)).toEqual({ path: "/b.ts", line: 1 });
    expect(nav.back()).toBe(true);
    expect(visited.at(-1)).toEqual({ path: "/a.ts", line: 1 });
    expect(nav.back()).toBe(false); // at the oldest entry

    expect(nav.forward()).toBe(true);
    expect(visited.at(-1)).toEqual({ path: "/b.ts", line: 1 });
  });

  it("coalesces small same-file moves into the current entry", () => {
    nav.record({ path: "/a.ts", line: 5 });
    nav.record({ path: "/a.ts", line: 8 }); // < JUMP_LINES → updates in place, no new entry
    expect(nav.back()).toBe(false); // still a single entry

    nav.record({ path: "/a.ts", line: 50 }); // ≥ JUMP_LINES → a fresh jump
    expect(nav.back()).toBe(true);
    expect(visited.at(-1)).toEqual({ path: "/a.ts", line: 8 }); // returns to the latest small-move position
  });

  it("drops forward history when a fresh jump follows a back step", () => {
    nav.record({ path: "/a.ts", line: 1 });
    nav.record({ path: "/b.ts", line: 1 });
    nav.record({ path: "/c.ts", line: 1 });

    expect(nav.back()).toBe(true); // → b
    nav.record({ path: "/d.ts", line: 1 }); // new jump from b truncates c
    expect(nav.forward()).toBe(false); // nothing ahead of d

    expect(nav.back()).toBe(true);
    expect(visited.at(-1)).toEqual({ path: "/b.ts", line: 1 });
  });

  it("coalesces the settle that follows a step, preserving the other side's history", () => {
    nav.record({ path: "/a.ts", line: 1 });
    nav.record({ path: "/b.ts", line: 1 });

    expect(nav.back()).toBe(true); // navigate to a
    nav.record({ path: "/a.ts", line: 1 }); // the cursor settle the nav itself triggers — coalesces in place

    expect(nav.forward()).toBe(true); // forward to b survived (history wasn't truncated)
    expect(visited.at(-1)).toEqual({ path: "/b.ts", line: 1 });
  });

  it("caps the history, dropping the oldest entries", () => {
    for (let i = 0; i < 60; i++) {
      nav.record({ path: `/f${i}.ts`, line: 1 });
    }
    let steps = 0;
    while (nav.back()) {
      steps += 1;
    }
    expect(steps).toBe(49); // 50 retained entries → 49 back-steps from the newest
    expect(visited.at(-1)).toEqual({ path: "/f10.ts", line: 1 }); // /f0../f9 fell off the front
  });
});
