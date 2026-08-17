import { describe, expect, it } from "vitest";
import {
  applyEdit,
  type BlameCommit,
  type BlameSnapshot,
  blameAt,
  blameLabel,
  relativeTime,
  startsRun,
} from "./blame-model";

function commit(sha: string, over: Partial<BlameCommit> = {}): BlameCommit {
  return {
    sha,
    author: "Kapps",
    email: "kapps@example.com",
    time: 1_700_000_000,
    summary: `subject ${sha}`,
    uncommitted: false,
    ...over,
  };
}

// One distinct commit per line, so a test can tell which line survived a seam — with a shared commit the
// mis-attribution this guards against is invisible.
function snapshot(): BlameSnapshot {
  return {
    commits: [commit("aaa"), commit("bbb"), commit("ccc")],
    lineCommits: [0, 1, 2],
    lineOriginals: [1, 2, 3],
  };
}

const shas = (s: BlameSnapshot): (string | null)[] =>
  s.lineCommits.map((_, index) => blameAt(s, index + 1)?.commit.sha ?? null);

describe("applyEdit", () => {
  it("keeps every line's attribution when nothing changes line count", () => {
    // Typing within line 2: the range starts and ends on it, so no whole line is added or removed.
    const next = applyEdit(snapshot(), {
      startLine: 2,
      removedLines: 0,
      addedLines: 0,
      fromLineStart: false,
    });

    expect(shas(next)).toEqual(["aaa", "bbb", "ccc"]);
  });

  it("splitting a line mid-way keeps the head's attribution and leaves the new line unattributed", () => {
    const next = applyEdit(snapshot(), {
      startLine: 1,
      removedLines: 0,
      addedLines: 2,
      fromLineStart: false,
    });

    expect(shas(next)).toEqual(["aaa", null, null, "bbb", "ccc"]);
  });

  it("inserting at the start of a line pushes that line down rather than displacing it", () => {
    // Pasting above line 2: the new lines take its place and it moves down still wearing its own commit.
    const next = applyEdit(snapshot(), {
      startLine: 2,
      removedLines: 0,
      addedLines: 2,
      fromLineStart: true,
    });

    expect(shas(next)).toEqual(["aaa", null, null, "bbb", "ccc"]);
  });

  it("deleting whole lines leaves the surviving line wearing its own commit", () => {
    // Select from the start of line 1 to the start of line 3 and delete: lines 1-2 go, line 3 survives as
    // line 1. It must keep "ccc" — the seam is where a naive splice hands it the deleted line's commit.
    const next = applyEdit(snapshot(), {
      startLine: 1,
      removedLines: 2,
      addedLines: 0,
      fromLineStart: true,
    });

    expect(shas(next)).toEqual(["ccc"]);
  });

  it("keeps the head's attribution when a mid-line selection spanning lines is replaced", () => {
    // From mid-line 1 to mid-line 3: the merged line begins with line 1's text, so it keeps "aaa".
    const next = applyEdit(snapshot(), {
      startLine: 1,
      removedLines: 2,
      addedLines: 0,
      fromLineStart: false,
    });

    expect(shas(next)).toEqual(["aaa"]);
  });

  it("carries each line's original number with it", () => {
    const next = applyEdit(snapshot(), {
      startLine: 1,
      removedLines: 0,
      addedLines: 1,
      fromLineStart: false,
    });

    expect(blameAt(next, 1)?.originalLine).toBe(1);
    expect(blameAt(next, 2)).toBeNull();
    expect(blameAt(next, 3)?.originalLine).toBe(2);
  });
});

describe("startsRun", () => {
  // Five lines: one commit owns 1-3, another 4-5 — the shape that made annotating every line unreadable.
  const runs: BlameSnapshot = {
    commits: [commit("aaa"), commit("bbb")],
    lineCommits: [0, 0, 0, 1, 1],
    lineOriginals: [1, 2, 3, 4, 5],
  };

  it("marks only the first line of each run", () => {
    expect([1, 2, 3, 4, 5].map((line) => startsRun(runs, line))).toEqual([
      true,
      false,
      false,
      true,
      false,
    ]);
  });

  it("restarts a run that a locally typed line interrupts", () => {
    const typed: BlameSnapshot = {
      commits: [commit("aaa")],
      lineCommits: [0, -1, 0],
      lineOriginals: [1, 0, 2],
    };

    // The line below the insert opens a new run: its neighbour above belongs to no commit.
    expect([1, 2, 3].map((line) => startsRun(typed, line))).toEqual([true, false, true]);
  });

  it("is false for a line with no attribution or past the end", () => {
    expect(startsRun(runs, 6)).toBe(false);
    expect(startsRun(runs, 0)).toBe(false);
  });
});

describe("blameAt", () => {
  it("has nothing for a line past the end of the blame", () => {
    expect(blameAt(snapshot(), 4)).toBeNull();
    expect(blameAt(snapshot(), 0)).toBeNull();
  });
});

describe("blameLabel", () => {
  const now = 1_700_000_000;

  it("names the author, when, and why", () => {
    expect(blameLabel(commit("aaa", { summary: "Fix the drain race" }), now + 3 * 86_400)).toBe(
      "Kapps, 3 days ago • Fix the drain race",
    );
  });

  it("says only that a working-tree line isn't committed", () => {
    expect(blameLabel(commit("0000", { uncommitted: true }), now)).toBe("Uncommitted changes");
  });

  it("truncates a subject that would run past the code it annotates", () => {
    const label = blameLabel(commit("aaa", { summary: "x".repeat(120) }), now);

    expect(label.endsWith("…")).toBe(true);
    expect(label.length).toBeLessThan(100);
  });
});

describe("relativeTime", () => {
  const now = 1_700_000_000;

  it.each([
    [30, "just now"],
    [90, "1 minute ago"],
    [3 * 3600, "3 hours ago"],
    [86_400, "1 day ago"],
    [3 * 86_400, "3 days ago"],
    [10 * 86_400, "1 week ago"],
    [60 * 86_400, "2 months ago"],
    [400 * 86_400, "1 year ago"],
  ])("reads %i seconds back as %s", (seconds, expected) => {
    expect(relativeTime(now - seconds, now)).toBe(expected);
  });

  it("never reads a clock-skewed future commit as negative", () => {
    expect(relativeTime(now + 5_000, now)).toBe("just now");
  });
});
