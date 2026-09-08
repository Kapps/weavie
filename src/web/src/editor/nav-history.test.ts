import { beforeEach, describe, expect, it, vi } from "vitest";
import { createNavHistory, type NavHistory, type NavLocation } from "./nav-history";

// Drains microtasks + timers so the in-flight guard (released in navigateTo's .finally) clears between a
// step and the next user action — mirroring a real back/forward whose async model swap has landed.
const settle = (): Promise<void> => new Promise((resolve) => setTimeout(resolve));

let nav: NavHistory;
let visited: NavLocation[];

beforeEach(() => {
  visited = [];
  nav = createNavHistory(
    (loc) => {
      visited.push(loc);
      return Promise.resolve();
    },
    () => {},
  );
});

describe("createNavHistory", () => {
  it("does not step when there is no history", () => {
    expect(nav.canBack()).toBe(false);
    expect(nav.canForward()).toBe(false);
    expect(nav.back()).toBe(false);
    expect(nav.forward()).toBe(false);
    expect(visited).toEqual([]);
  });

  it("steps back and forward through recorded jumps", async () => {
    nav.record({ kind: "file", path: "/a.ts", line: 1 });
    expect(nav.canBack()).toBe(false);
    nav.record({ kind: "file", path: "/b.ts", line: 1 });
    expect(nav.canBack()).toBe(true);
    nav.record({ kind: "file", path: "/c.ts", line: 1 });

    expect(nav.back()).toBe(true);
    expect(nav.canForward()).toBe(true);
    expect(visited.at(-1)).toEqual({ kind: "file", path: "/b.ts", line: 1 });
    await settle();
    expect(nav.back()).toBe(true);
    expect(visited.at(-1)).toEqual({ kind: "file", path: "/a.ts", line: 1 });
    await settle();
    expect(nav.back()).toBe(false); // at the oldest entry

    expect(nav.forward()).toBe(true);
    expect(visited.at(-1)).toEqual({ kind: "file", path: "/b.ts", line: 1 });
  });

  it("coalesces small same-file moves into the current entry", () => {
    nav.record({ kind: "file", path: "/a.ts", line: 5 });
    nav.record({ kind: "file", path: "/a.ts", line: 8 }); // < JUMP_LINES → updates in place, no new entry
    expect(nav.back()).toBe(false); // still a single entry

    nav.record({ kind: "file", path: "/a.ts", line: 50 }); // ≥ JUMP_LINES → a fresh jump
    expect(nav.back()).toBe(true);
    expect(visited.at(-1)).toEqual({ kind: "file", path: "/a.ts", line: 8 }); // returns to the latest small-move position
  });

  it("drops forward history when a fresh jump follows a back step", async () => {
    nav.record({ kind: "file", path: "/a.ts", line: 1 });
    nav.record({ kind: "file", path: "/b.ts", line: 1 });
    nav.record({ kind: "file", path: "/c.ts", line: 1 });

    expect(nav.back()).toBe(true); // → b
    await settle(); // the step's swap lands; the guard clears
    nav.record({ kind: "file", path: "/d.ts", line: 1 }); // new jump from b truncates c
    expect(nav.forward()).toBe(false); // nothing ahead of d

    expect(nav.back()).toBe(true);
    expect(visited.at(-1)).toEqual({ kind: "file", path: "/b.ts", line: 1 });
  });

  it("suppresses a settle-record fired mid-swap, so a step never truncates the far side", async () => {
    // Model the real async model swap: navigateTo resolves only when we release it, and a debounced
    // settle-record fires *during* the swap while the editor still reports the previous file.
    let releaseSwap: (() => void) | undefined;
    visited = [];
    nav = createNavHistory(
      (loc) => {
        visited.push(loc);
        return new Promise<void>((resolve) => {
          releaseSwap = resolve;
        });
      },
      () => {},
    );
    nav.record({ kind: "file", path: "/a.ts", line: 1 });
    nav.record({ kind: "file", path: "/b.ts", line: 1 }); // [a, b], at b

    expect(nav.back()).toBe(true); // step to a; the swap is in flight
    nav.record({ kind: "file", path: "/b.ts", line: 1 }); // mid-swap settle still reports b — must be ignored
    releaseSwap?.();
    await settle();

    // Without the in-flight guard this record would truncate the forward stack and Forward would be dead.
    expect(nav.forward()).toBe(true);
    expect(visited.at(-1)).toEqual({ kind: "file", path: "/b.ts", line: 1 });
  });

  it("inserts the scrolled-to position when leaving a file the cursor has scrolled off", async () => {
    // Go-to-def landed at a.ts:10; the user then scrolled to ~line 100 (cursor left off-screen) before
    // jumping into b.ts. The leave-record captures the viewport, so Back visits 100 before 10.
    nav.record({ kind: "file", path: "/a.ts", line: 10 });
    nav.record({ kind: "file", path: "/a.ts", line: 100 }); // viewport centre recorded on leaving a.ts
    nav.record({ kind: "file", path: "/b.ts", line: 5 });

    expect(nav.back()).toBe(true);
    expect(visited.at(-1)).toEqual({ kind: "file", path: "/a.ts", line: 100 });
    await settle();
    expect(nav.back()).toBe(true);
    expect(visited.at(-1)).toEqual({ kind: "file", path: "/a.ts", line: 10 });
  });

  it("caps the history, dropping the oldest entries", () => {
    for (let i = 0; i < 60; i++) {
      nav.record({ kind: "file", path: `/f${i}.ts`, line: 1 });
    }
    let steps = 0;
    while (nav.back()) {
      steps += 1;
    }
    expect(steps).toBe(49); // 50 retained entries → 49 back-steps from the newest
    expect(visited.at(-1)).toEqual({ kind: "file", path: "/f10.ts", line: 1 }); // /f0../f9 fell off the front
  });
});

describe("surface navigation", () => {
  it("keeps the same file and line in review and file mode as distinct destinations", async () => {
    const origin: NavLocation = {
      kind: "review",
      path: "/a.ts",
      line: 8,
      anchor: { line: 8, offset: -37 },
    };
    nav.record(origin);
    nav.record({ kind: "file", path: "/a.ts", line: 8 });
    expect(nav.back()).toBe(true);
    expect(visited.at(-1)).toEqual(origin);
    await settle();
    expect(nav.forward()).toBe(true);
    expect(visited.at(-1)?.kind).toBe("file");
  });

  it("explicit nearby jumps preserve the origin despite cursor coalescing", () => {
    nav.record({ kind: "review", path: "/a.ts", line: 8 });
    nav.push({ kind: "review", path: "/a.ts", line: 9 });
    expect(nav.back()).toBe(true);
    expect(visited.at(-1)?.line).toBe(8);
  });

  it("failed restoration retains the actual current location and notifies after rollback", async () => {
    const changed = vi.fn();
    const history = createNavHistory(async () => {
      throw new Error("review removed");
    }, changed);
    history.record({ kind: "review", path: "/a.ts", line: 8 });
    history.record({ kind: "file", path: "/b.ts", line: 1 });
    expect(history.back()).toBe(true);
    await settle();
    expect(history.canBack()).toBe(true);
    expect(history.canForward()).toBe(false);
    expect(changed).toHaveBeenCalledOnce();
  });
});

it("a completed replacement traversal releases recording while an obsolete read remains unresolved", async () => {
  let release!: () => void;
  let calls = 0;
  const visited: NavLocation[] = [];
  const delayed = new Promise<void>((resolve) => {
    release = resolve;
  });
  const history = createNavHistory(
    async (location) => {
      visited.push(location);
      if (++calls === 1) await delayed;
    },
    () => {},
  );
  history.record({ kind: "review", path: "/a.ts", line: 8 });
  history.record({ kind: "file", path: "/b.ts", line: 1 });
  history.record({ kind: "file", path: "/c.ts", line: 1 });
  history.back();
  history.back();
  await settle();
  expect(history.canRecord()).toBe(true);
  history.record({ kind: "file", path: "/d.ts", line: 1 });
  release();
  await settle();
  expect(history.canForward()).toBe(false);
  expect(history.back()).toBe(true);
  expect(visited.at(-1)?.path).toBe("/a.ts");
});
