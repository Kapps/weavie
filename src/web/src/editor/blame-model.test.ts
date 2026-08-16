import { describe, expect, it } from "vitest";
import {
  applyEdit,
  type BlameCommit,
  type BlameSnapshot,
  blameAt,
  blameLabel,
  relativeTime,
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

// Three lines, the middle one from a different commit — enough to tell a shift from a drop.
function snapshot(): BlameSnapshot {
  return {
    commits: [commit("aaa"), commit("bbb")],
    lineCommits: [0, 1, 0],
    lineOriginals: [1, 2, 3],
  };
}

const shas = (s: BlameSnapshot): (string | null)[] =>
  s.lineCommits.map((_, index) => blameAt(s, index + 1)?.commit.sha ?? null);

describe("applyEdit", () => {
  it("keeps every line's attribution when nothing changes line count", () => {
    // Typing within line 2: the range starts and ends on it, so no whole line is added or removed.
    const next = applyEdit(snapshot(), { startLine: 2, removedLines: 0, addedLines: 0 });

    expect(shas(next)).toEqual(["aaa", "bbb", "aaa"]);
  });

  it("pushes later lines down and leaves the inserted ones unattributed", () => {
    const next = applyEdit(snapshot(), { startLine: 1, removedLines: 0, addedLines: 2 });

    // The line the edit started on keeps its commit; the two new lines belong to no commit yet.
    expect(shas(next)).toEqual(["aaa", null, null, "bbb", "aaa"]);
  });

  it("pulls later lines up when whole lines are deleted", () => {
    const next = applyEdit(snapshot(), { startLine: 1, removedLines: 2, addedLines: 0 });

    expect(shas(next)).toEqual(["aaa"]);
  });

  it("keeps the anchor's attribution when a multi-line selection is replaced", () => {
    const next = applyEdit(snapshot(), { startLine: 1, removedLines: 2, addedLines: 1 });

    expect(shas(next)).toEqual(["aaa", null]);
  });

  it("carries each line's original number with it", () => {
    const next = applyEdit(snapshot(), { startLine: 1, removedLines: 0, addedLines: 1 });

    expect(blameAt(next, 1)?.originalLine).toBe(1);
    expect(blameAt(next, 2)).toBeNull();
    expect(blameAt(next, 3)?.originalLine).toBe(2);
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
